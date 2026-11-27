import ast
import difflib
import inspect
import json
import sys


def build_runtime_index():
    import vapoursynth as vs

    core = vs.core
    core_members = {}
    namespaces = {}
    function_params = {}  # (namespace, function_name) -> (set_of_param_names, has_var_kwargs)
    video_node_attrs = set(dir(getattr(vs, "VideoNode", object)))

    for name in dir(core):
        if name.startswith("_"):
            continue

        try:
            item = getattr(core, name)
        except Exception:
            continue

        item_type = type(item).__name__
        if item_type == "Plugin":
            namespace = getattr(item, "namespace", name)
            functions = set()
            for function_name in dir(item):
                if function_name.startswith("_"):
                    continue

                try:
                    function = getattr(item, function_name)
                except Exception:
                    continue

                if callable(function):
                    functions.add(function_name)
                    try:
                        sig = inspect.signature(function)
                        param_names = set()
                        has_kwargs = False
                        for param in sig.parameters.values():
                            if param.kind == inspect.Parameter.VAR_KEYWORD:
                                has_kwargs = True
                            elif param.kind != inspect.Parameter.VAR_POSITIONAL:
                                param_names.add(param.name)
                        function_params[(namespace, function_name)] = (param_names, has_kwargs)
                    except Exception:
                        pass

            namespaces[namespace] = functions
            core_members[namespace] = "namespace"
            continue

        if callable(item):
            core_members[name] = "method"
            continue

        core_members[name] = "property"

    core_version = getattr(vs, "__version__", None)
    if isinstance(core_version, (tuple, list)) and core_version:
        version_label = ".".join(str(part) for part in core_version)
    else:
        version_label = str(core_version or "unknown")

    return {
        "runtimeSummary": f"VapourSynth {version_label} / {len(namespaces)} plugins",
        "coreMembers": core_members,
        "namespaces": namespaces,
        "functionParams": function_params,
        "videoNodeAttrs": video_node_attrs,
    }


def build_syntax_diagnostic(ex):
    start_line = ex.lineno or 1
    start_column = ex.offset or 1
    end_line = ex.end_lineno or start_line
    end_column = ex.end_offset or max(start_column + 1, 2)

    return {
        "code": "python-syntax",
        "severity": "error",
        "message": ex.msg or str(ex),
        "startLine": start_line,
        "startColumn": start_column,
        "endLine": end_line,
        "endColumn": end_column,
        "source": "python",
        "relatedText": (ex.text or "").strip()
    }


def assign_parents(root):
    for parent in ast.walk(root):
        for child in ast.iter_child_nodes(parent):
            child.parent = parent


def extract_attribute_chain(node):
    parts = []
    current = node

    while isinstance(current, ast.Attribute):
        parts.append(current.attr)
        current = current.value

    if isinstance(current, ast.Name):
        parts.append(current.id)
        parts.reverse()
        return parts

    return None


def normalize_chain(parts):
    if not parts:
        return None

    if len(parts) >= 2 and parts[0] == "vs" and parts[1] == "core":
        return ["core", *parts[2:]]

    if parts[0] == "core":
        return parts

    return None


def build_unknown_member_message(kind, name, candidates):
    suggestion = difflib.get_close_matches(name, candidates, n=1)
    if suggestion:
        return f"Unknown VapourSynth {kind} '{name}'. Did you mean '{suggestion[0]}'?", suggestion[0]

    return f"Unknown VapourSynth {kind} '{name}'.", None


def _utf8_col_to_utf16_col(source_lines, lineno, byte_col):
    if byte_col is None or byte_col < 0:
        return 1
    if 1 <= lineno <= len(source_lines):
        line = source_lines[lineno - 1]
        prefix = line.encode("utf-8")[:byte_col].decode("utf-8", errors="replace")
        return len(prefix.encode("utf-16-le")) // 2 + 1
    return byte_col + 1


def _utf16_len(text):
    return len(text.encode("utf-16-le")) // 2


def locate_segment(node, source, segment):
    source_lines = source.splitlines()
    target = node
    while isinstance(target, ast.Attribute) and target.attr != segment:
        target = target.value

    if isinstance(target, ast.Attribute) and target.attr == segment and getattr(target, "end_col_offset", None) is not None:
        end_line = getattr(target, "end_lineno", target.lineno) or target.lineno
        end_byte = target.end_col_offset
        start_byte = max(0, end_byte - len(segment.encode("utf-8")))
        start_col = _utf8_col_to_utf16_col(source_lines, end_line, start_byte)
        end_col = _utf8_col_to_utf16_col(source_lines, end_line, end_byte)
        return {
            "startLine": end_line,
            "startColumn": start_col,
            "endLine": end_line,
            "endColumn": max(start_col + 1, end_col)
        }

    start_line = getattr(target, "lineno", 1) or 1
    start_byte = getattr(target, "col_offset", 0) or 0
    if isinstance(target, ast.Name) and target.id == segment:
        start_col = _utf8_col_to_utf16_col(source_lines, start_line, start_byte)
        end_col = start_col + max(1, _utf16_len(segment))
        return {
            "startLine": start_line,
            "startColumn": start_col,
            "endLine": start_line,
            "endColumn": end_col
        }

    segment_text = ast.get_source_segment(source, target) or segment
    relative_index = segment_text.find(segment)
    if relative_index > 0:
        prefix_text = segment_text[:relative_index]
        newline_count = prefix_text.count("\n")
        if newline_count > 0:
            start_line += newline_count
            last_line_prefix = prefix_text.rsplit("\n", 1)[-1]
            start_col = _utf16_len(last_line_prefix) + 1
        else:
            start_col = _utf8_col_to_utf16_col(source_lines, start_line, start_byte) + _utf16_len(prefix_text)
    else:
        start_col = _utf8_col_to_utf16_col(source_lines, start_line, start_byte)
    end_col = start_col + max(1, _utf16_len(segment))

    return {
        "startLine": start_line,
        "startColumn": start_col,
        "endLine": start_line,
        "endColumn": end_col
    }


def _collect_target_names(target):
    names = []
    if isinstance(target, ast.Name):
        names.append(target.id)
    elif isinstance(target, (ast.Tuple, ast.List)):
        for elt in target.elts:
            names.extend(_collect_target_names(elt))
    elif isinstance(target, ast.Starred):
        names.extend(_collect_target_names(target.value))
    return names


def _collect_top_level_clip_and_import_names(tree):
    imported_names = set()
    clip_vars = set()
    non_clip_names = {"core", "vs", "os", "sys", "math", "Path", "path"}
    for stmt in tree.body:
        if isinstance(stmt, ast.Import):
            for alias in stmt.names:
                imported_names.add(alias.asname or alias.name.split(".")[0])
        elif isinstance(stmt, ast.ImportFrom):
            for alias in stmt.names:
                imported_names.add(alias.asname or alias.name)
        elif isinstance(stmt, ast.Assign):
            for target in stmt.targets:
                for name in _collect_target_names(target):
                    if name not in non_clip_names:
                        clip_vars.add(name)
        elif isinstance(stmt, ast.AnnAssign):
            for name in _collect_target_names(stmt.target):
                if name not in non_clip_names:
                    clip_vars.add(name)
    return clip_vars, imported_names


def _locate_keyword_name(kw, source):
    source_lines = source.splitlines()
    lineno = getattr(kw, "lineno", 1) or 1
    seg = ast.get_source_segment(source, kw) or kw.arg or ""
    eq_idx = seg.find("=")
    arg_text = seg[:eq_idx].strip() if eq_idx >= 0 else (kw.arg or "")
    start_col = _utf8_col_to_utf16_col(source_lines, lineno, getattr(kw, "col_offset", 0))
    return {
        "startLine": lineno,
        "startColumn": start_col,
        "endLine": lineno,
        "endColumn": start_col + max(1, _utf16_len(arg_text or kw.arg or "")),
    }


def _locate_ast_node(node, source):
    source_lines = source.splitlines()
    start_line = getattr(node, "lineno", 1) or 1
    start_col = _utf8_col_to_utf16_col(source_lines, start_line, getattr(node, "col_offset", 0))
    end_line = getattr(node, "end_lineno", start_line) or start_line
    raw_end_col = getattr(node, "end_col_offset", None)
    if raw_end_col is not None:
        end_col = _utf8_col_to_utf16_col(source_lines, end_line, raw_end_col)
    else:
        end_col = start_col + max(1, _utf16_len(ast.get_source_segment(source, node) or ""))
    return {
        "startLine": start_line,
        "startColumn": start_col,
        "endLine": end_line,
        "endColumn": max(start_col + 1 if end_line == start_line else 1, end_col),
    }


def build_static_diagnostics(tree, source, runtime_index):
    diagnostics = []
    seen = set()

    assign_parents(tree)
    clip_vars, imported_names = _collect_top_level_clip_and_import_names(tree)
    namespaces_map = runtime_index["namespaces"]
    function_params = runtime_index.get("functionParams", {})
    video_node_attrs = runtime_index.get("videoNodeAttrs", set())

    def add_diag(diagnostic):
        key = (
            diagnostic["code"],
            diagnostic["startLine"],
            diagnostic["startColumn"],
            diagnostic["endLine"],
            diagnostic["endColumn"],
            diagnostic["message"]
        )
        if key not in seen:
            seen.add(key)
            diagnostics.append(diagnostic)

    for node in ast.walk(tree):
        if isinstance(node, ast.Attribute):
            if isinstance(getattr(node, "parent", None), ast.Attribute):
                continue

            raw_chain = extract_attribute_chain(node)
            if not raw_chain or len(raw_chain) < 2:
                continue

            chain = normalize_chain(raw_chain)
            if chain and len(chain) >= 2 and chain[0] == "core":
                root_member = chain[1]
                if len(chain) == 2:
                    if root_member in runtime_index["coreMembers"]:
                        continue
                    msg, suggestion = build_unknown_member_message(
                        "core member",
                        root_member,
                        list(runtime_index["coreMembers"].keys()))
                    location = locate_segment(node, source, root_member)
                    diag = {
                        "code": "vapoursynth-core-member",
                        "severity": "error",
                        "message": msg,
                        "source": "vapoursynth",
                        "relatedText": ast.get_source_segment(source, node) or root_member,
                        **location
                    }
                    if suggestion:
                        diag["quickFixTitle"] = f"Replace '{root_member}' with '{suggestion}'"
                        diag["suggestedReplacement"] = suggestion
                    add_diag(diag)
                else:
                    namespace = chain[1]
                    function = chain[2]
                    functions = namespaces_map.get(namespace)
                    if functions is None:
                        msg, suggestion = build_unknown_member_message(
                            "plugin namespace",
                            namespace,
                            list(namespaces_map.keys()))
                        location = locate_segment(node, source, namespace)
                        diag = {
                            "code": "vapoursynth-namespace",
                            "severity": "error",
                            "message": msg,
                            "source": "vapoursynth",
                            "relatedText": ast.get_source_segment(source, node) or namespace,
                            **location
                        }
                        if suggestion:
                            diag["quickFixTitle"] = f"Replace '{namespace}' with '{suggestion}'"
                            diag["suggestedReplacement"] = suggestion
                        add_diag(diag)
                    elif function not in functions:
                        msg, suggestion = build_unknown_member_message(
                            "function",
                            function,
                            sorted(functions))
                        location = locate_segment(node, source, function)
                        diag = {
                            "code": "vapoursynth-function",
                            "severity": "error",
                            "message": msg,
                            "source": "vapoursynth",
                            "relatedText": ast.get_source_segment(source, node) or function,
                            **location
                        }
                        if suggestion:
                            diag["quickFixTitle"] = f"Replace '{function}' with '{suggestion}'"
                            diag["suggestedReplacement"] = suggestion
                        add_diag(diag)
            elif (
                len(raw_chain) >= 3
                and raw_chain[0] in clip_vars
                and raw_chain[0] not in imported_names
            ):
                # Chained VideoNode call: clip.<namespace>.<function>
                ns = raw_chain[1]
                fn = raw_chain[2]
                if ns in namespaces_map:
                    functions = namespaces_map[ns]
                    if fn not in functions:
                        msg, suggestion = build_unknown_member_message("function", fn, sorted(functions))
                        location = locate_segment(node, source, fn)
                        diag = {
                            "code": "vapoursynth-chained-function",
                            "severity": "error",
                            "message": msg,
                            "source": "vapoursynth",
                            "relatedText": ast.get_source_segment(source, node) or fn,
                            **location
                        }
                        if suggestion:
                            diag["quickFixTitle"] = f"Replace '{fn}' with '{suggestion}'"
                            diag["suggestedReplacement"] = suggestion
                        add_diag(diag)
                elif ns not in video_node_attrs:
                    close_ns = difflib.get_close_matches(ns, list(namespaces_map.keys()), n=1, cutoff=0.72)
                    if close_ns:
                        suggestion = close_ns[0]
                        location = locate_segment(node, source, ns)
                        add_diag({
                            "code": "vapoursynth-chained-namespace",
                            "severity": "error",
                            "message": f"Unknown VapourSynth plugin namespace '{ns}' on clip '{raw_chain[0]}'. Did you mean '{suggestion}'?",
                            "source": "vapoursynth",
                            "relatedText": ast.get_source_segment(source, node) or ns,
                            "quickFixTitle": f"Replace '{ns}' with '{suggestion}'",
                            "suggestedReplacement": suggestion,
                            **location
                        })
            continue

        if isinstance(node, ast.Call) and isinstance(node.func, ast.Attribute):
            raw_chain = extract_attribute_chain(node.func)
            if not raw_chain or len(raw_chain) < 3:
                continue
            norm = normalize_chain(raw_chain)
            if norm and norm[0] == "core" and len(norm) >= 3:
                ns, fn = norm[1], norm[2]
            elif raw_chain[0] in clip_vars and raw_chain[0] not in imported_names and raw_chain[1] in namespaces_map:
                ns, fn = raw_chain[1], raw_chain[2]
            else:
                continue

            sig_info = function_params.get((ns, fn))
            if sig_info is not None:
                valid_params, has_kwargs = sig_info
                if not has_kwargs:
                    for kw in node.keywords:
                        if kw.arg is None:
                            continue
                        if kw.arg not in valid_params:
                            close = difflib.get_close_matches(kw.arg, sorted(valid_params), n=1, cutoff=0.55)
                            suggestion = close[0] if close else None
                            msg = (
                                f"Unknown parameter '{kw.arg}' for {ns}.{fn}. Did you mean '{suggestion}'?"
                                if suggestion
                                else f"Unknown parameter '{kw.arg}' for {ns}.{fn}."
                            )
                            loc = _locate_keyword_name(kw, source)
                            diag = {
                                "code": "vapoursynth-unknown-kwarg",
                                "severity": "error",
                                "message": msg,
                                "source": "vapoursynth",
                                "relatedText": f"{ns}.{fn}({kw.arg}=...)",
                                **loc
                            }
                            if suggestion:
                                diag["quickFixTitle"] = f"Replace '{kw.arg}' with '{suggestion}'"
                                diag["suggestedReplacement"] = suggestion
                            add_diag(diag)

            # Check odd pixel crop values on std.CropRel / std.CropAbs / std.Crop
            if ns == "std" and fn in ("CropRel", "CropAbs", "Crop"):
                for kw in node.keywords:
                    if kw.arg in ("left", "right", "top", "bottom", "width", "height"):
                        if isinstance(kw.value, ast.Constant) and isinstance(kw.value.value, int) and not isinstance(kw.value.value, bool):
                            val = kw.value.value
                            if val > 0 and (val % 2) == 1:
                                even_val = max(0, val - 1) if kw.arg not in ("width", "height") else max(2, val - 1)
                                loc = _locate_ast_node(kw.value, source)
                                add_diag({
                                    "code": "vapoursynth-odd-crop",
                                    "severity": "warning",
                                    "message": f"Odd {kw.arg}={val} in {ns}.{fn} fails on YUV420 clips (even values required).",
                                    "source": "vapoursynth-flow",
                                    "relatedText": f"{kw.arg}={val}",
                                    "quickFixTitle": f"Round {kw.arg} to even ({even_val})",
                                    "suggestedReplacement": str(even_val),
                                    **loc
                                })

    diagnostics.sort(key=lambda item: (
        item["startLine"],
        item["startColumn"],
        item["code"]))
    return diagnostics


def _collect_load_names(node):
    names = set()
    for sub in ast.walk(node):
        if isinstance(sub, ast.Name) and isinstance(sub.ctx, ast.Load):
            names.add(sub.id)
    return names


def _extract_set_output_call(stmt):
    if not isinstance(stmt, ast.Expr) or not isinstance(stmt.value, ast.Call):
        return None
    call = stmt.value
    # Pattern: var.set_output(...)
    if isinstance(call.func, ast.Attribute) and call.func.attr == "set_output" and isinstance(call.func.value, ast.Name):
        idx = 0
        if call.args and isinstance(call.args[0], ast.Constant) and isinstance(call.args[0].value, int):
            idx = call.args[0].value
        for kw in call.keywords:
            if kw.arg == "index" and isinstance(kw.value, ast.Constant) and isinstance(kw.value.value, int):
                idx = kw.value.value
        return call.func.value, idx
    return None


def build_variable_flow_diagnostics(tree, source):
    diagnostics = []
    defined_vars = set()
    clip_vars_order = []
    derived_from = {}  # var -> set of upstream vars it depends on
    var_defined_line = {}
    used_after_def = set()
    output_calls = []  # (name_node, output_index, stmt_idx)
    imported_names = set()

    non_clip_names = {"core", "vs", "os", "sys", "math", "Path", "path"}

    for stmt_idx, stmt in enumerate(tree.body):
        if isinstance(stmt, ast.Import):
            for alias in stmt.names:
                name = alias.asname or alias.name.split(".")[0]
                defined_vars.add(name)
                imported_names.add(name)
            continue
        if isinstance(stmt, ast.ImportFrom):
            for alias in stmt.names:
                name = alias.asname or alias.name
                defined_vars.add(name)
                imported_names.add(name)
            continue
        if isinstance(stmt, (ast.FunctionDef, ast.AsyncFunctionDef, ast.ClassDef)):
            defined_vars.add(stmt.name)
            continue

        out_info = _extract_set_output_call(stmt)
        if out_info is not None:
            var_node, out_idx = out_info
            output_calls.append((var_node, out_idx, stmt_idx))
            used_after_def.add(var_node.id)
            continue

        if isinstance(stmt, ast.Assign):
            rhs_names = _collect_load_names(stmt.value)
            used_after_def.update(rhs_names)
            for target in stmt.targets:
                for lhs in _collect_target_names(target):
                    defined_vars.add(lhs)
                    var_defined_line[lhs] = stmt.lineno
                    if lhs not in non_clip_names:
                        if lhs not in clip_vars_order:
                            clip_vars_order.append(lhs)
                        deps = {n for n in rhs_names if n in defined_vars and n not in imported_names and n not in non_clip_names}
                        if lhs in derived_from:
                            derived_from[lhs].update(deps)
                        else:
                            derived_from[lhs] = set(deps)
            continue

        if isinstance(stmt, ast.AnnAssign):
            rhs_names = _collect_load_names(stmt.value) if stmt.value is not None else set()
            used_after_def.update(rhs_names)
            if stmt.value is not None:
                for lhs in _collect_target_names(stmt.target):
                    defined_vars.add(lhs)
                    var_defined_line[lhs] = stmt.lineno
                    if lhs not in non_clip_names:
                        if lhs not in clip_vars_order:
                            clip_vars_order.append(lhs)
                        deps = {n for n in rhs_names if n in defined_vars and n not in imported_names and n not in non_clip_names}
                        if lhs in derived_from:
                            derived_from[lhs].update(deps)
                        else:
                            derived_from[lhs] = set(deps)
            continue

        # Record bindings inside top-level control flow blocks (if/try/with/for/while/match)
        # without descending into function/class definitions.
        stack = [stmt]
        while stack:
            cur = stack.pop()
            if isinstance(cur, (ast.FunctionDef, ast.AsyncFunctionDef, ast.ClassDef)):
                defined_vars.add(cur.name)
                continue
            if isinstance(cur, ast.Name) and isinstance(cur.ctx, ast.Store):
                defined_vars.add(cur.id)
                var_defined_line.setdefault(cur.id, getattr(cur, "lineno", stmt.lineno))
                if cur.id not in non_clip_names and cur.id not in clip_vars_order:
                    clip_vars_order.append(cur.id)
            elif isinstance(cur, ast.Name) and isinstance(cur.ctx, ast.Load):
                used_after_def.add(cur.id)
            else:
                stack.extend(ast.iter_child_nodes(cur))

    # Build transitive ancestor lookup: ancestors_of[v] = all upstream clip variables v derives from.
    def get_ancestors(var_name):
        visited = set()
        stack = list(derived_from.get(var_name, ()))
        while stack:
            cur = stack.pop()
            if cur in visited or cur == var_name:
                continue
            visited.add(cur)
            stack.extend(derived_from.get(cur, ()))
        return visited

    # Track output indices to catch silent duplicate overwrites.
    seen_output_indices = {}
    all_output_vars = {node.id for node, _, _ in output_calls}

    for var_node, out_idx, _ in output_calls:
        var_name = var_node.id
        loc = locate_segment(var_node, source, var_name)

        if out_idx in seen_output_indices:
            prev_line = seen_output_indices[out_idx]
            diagnostics.append({
                "code": "vapoursynth-duplicate-output-index",
                "severity": "warning",
                "message": f"Output index {out_idx} overwrites earlier set_output({out_idx}) on line {prev_line}.",
                "source": "vapoursynth-flow",
                "relatedText": f"set_output({out_idx})",
                **loc,
            })
        else:
            seen_output_indices[out_idx] = var_node.lineno

        # Rule 1: Undefined output variable (e.g. renamed src8 -> src16 but forgot src8.set_output())
        if var_name not in defined_vars:
            candidates = [v for v in clip_vars_order if var_defined_line.get(v, 10**9) < var_node.lineno]
            close = difflib.get_close_matches(var_name, candidates, n=1, cutoff=0.4)
            suggestion = close[0] if close else (candidates[-1] if candidates else None)
            msg = (
                f"Output variable '{var_name}' is not defined. Did you mean '{suggestion}'?"
                if suggestion
                else f"Output variable '{var_name}' is not defined."
            )
            diag = {
                "code": "vapoursynth-undefined-output",
                "severity": "error",
                "message": msg,
                "source": "vapoursynth-flow",
                "relatedText": var_name,
                **loc,
            }
            if suggestion:
                diag["quickFixTitle"] = f"Replace '{var_name}' with '{suggestion}'"
                diag["suggestedReplacement"] = suggestion
            diagnostics.append(diag)
            continue

        # Rule 2: Stale upstream variable output when a derived downstream clip is unused
        # (e.g. src16 = core.fmtc.bitdepth(src8, bits=16) followed by src8.set_output())
        unused_descendants = []
        for cand in clip_vars_order:
            if cand == var_name or cand in all_output_vars or cand in used_after_def:
                continue
            if var_defined_line.get(cand, 10**9) >= var_node.lineno:
                continue
            if var_name in get_ancestors(cand):
                unused_descendants.append(cand)

        if unused_descendants:
            latest = unused_descendants[-1]
            diagnostics.append({
                "code": "vapoursynth-stale-output",
                "severity": "warning",
                "message": f"set_output({out_idx}) uses upstream clip '{var_name}', while derived clip '{latest}' is unused. Switch to '{latest}'?",
                "source": "vapoursynth-flow",
                "relatedText": f"{var_name} -> {latest}",
                "quickFixTitle": f"Switch set_output({out_idx}) to '{latest}'",
                "suggestedReplacement": latest,
                **loc,
            })

    # Rule 2b: Orphan clip/filter variable (assigned from a call, never referenced or output, not already reported as stale-output)
    reported_stale_targets = {
        d.get("suggestedReplacement")
        for d in diagnostics
        if d.get("code") == "vapoursynth-stale-output"
    }
    if output_calls:
        for stmt in tree.body:
            if not isinstance(stmt, ast.Assign) or not isinstance(stmt.value, ast.Call):
                continue
            for target in stmt.targets:
                if not isinstance(target, ast.Name):
                    continue
                lhs = target.id
                if (
                    lhs.startswith("_")
                    or lhs in non_clip_names
                    or lhs in all_output_vars
                    or lhs in used_after_def
                    or lhs in reported_stale_targets
                ):
                    continue
                loc = locate_segment(target, source, lhs)
                diagnostics.append({
                    "code": "vapoursynth-unused-clip",
                    "severity": "info",
                    "message": f"Variable '{lhs}' is assigned but never referenced or passed to set_output().",
                    "source": "vapoursynth-flow",
                    "relatedText": lhs,
                    **loc,
                })

    # Rule 3: Missing common VapourSynth imports (with Quick Fix inserting at top of file / after last import)
    all_bound_names = set(defined_vars)
    last_import_line = 0
    for stmt in tree.body:
        if isinstance(stmt, (ast.Import, ast.ImportFrom)):
            last_import_line = max(last_import_line, getattr(stmt, "end_lineno", stmt.lineno) or stmt.lineno)
    for node in ast.walk(tree):
        if isinstance(node, ast.Name) and isinstance(node.ctx, ast.Store):
            all_bound_names.add(node.id)
        elif isinstance(node, ast.arg):
            all_bound_names.add(node.arg)
        elif isinstance(node, ast.ExceptHandler) and node.name:
            all_bound_names.add(node.name)

    known_missing_imports = {
        "vs": ("import vapoursynth as vs\n", "Add 'import vapoursynth as vs'"),
        "core": (
            "core = vs.core\n" if "vs" in all_bound_names else "import vapoursynth as vs\ncore = vs.core\n",
            "Add 'core = vs.core'" if "vs" in all_bound_names else "Add 'import vapoursynth as vs' and 'core = vs.core'",
        ),
        "awf": ("import awsmfunc as awf\n", "Add 'import awsmfunc as awf'"),
        "haf": ("import havsfunc as haf\n", "Add 'import havsfunc as haf'"),
        "mvf": ("import mvsfunc as mvf\n", "Add 'import mvsfunc as mvf'"),
        "replace_ranges": ("from vstools import replace_ranges\n", "Add 'from vstools import replace_ranges'"),
    }

    reported_missing = set()
    insert_line = last_import_line + 1 if last_import_line > 0 else 1
    for node in ast.walk(tree):
        if not isinstance(node, ast.Name) or not isinstance(node.ctx, ast.Load):
            continue
        sym = node.id
        if sym in all_bound_names or sym not in known_missing_imports or sym in reported_missing:
            continue
        reported_missing.add(sym)
        insert_text, fix_title = known_missing_imports[sym]
        loc = locate_segment(node, source, sym)
        diagnostics.append({
            "code": "vapoursynth-missing-import",
            "severity": "warning",
            "message": f"'{sym}' is not imported or defined. {fix_title}?",
            "source": "vapoursynth-flow",
            "relatedText": sym,
            "quickFixTitle": fix_title,
            "quickFixInsertLine": insert_line,
            "quickFixInsertText": insert_text,
            **loc,
        })

    return diagnostics


def _acquire_protocol_stdout():
    try:
        import os
        saved_fd = os.dup(1)
        os.dup2(2, 1)
        sys.stdout = sys.stderr
        return os.fdopen(saved_fd, "w", encoding="utf-8", errors="backslashreplace")
    except Exception:
        return sys.stdout


def main():
    protocol_stdout = _acquire_protocol_stdout()
    payload = json.load(sys.stdin)
    file_path = payload.get("filePath") or "<untitled.vpy>"
    content = payload.get("content") or ""

    try:
        tree = ast.parse(content, filename=file_path)
    except SyntaxError as ex:
        result = {
            "isRuntimeReady": False,
            "runtimeSummary": "Python syntax error",
            "diagnostics": [build_syntax_diagnostic(ex)]
        }
        json.dump(result, protocol_stdout, ensure_ascii=False)
        protocol_stdout.flush()
        return

    flow_diagnostics = build_variable_flow_diagnostics(tree, content)

    try:
        runtime_index = build_runtime_index()
        diagnostics = build_static_diagnostics(tree, content, runtime_index) + flow_diagnostics
        diagnostics.sort(key=lambda item: (item["startLine"], item["startColumn"], item["code"]))
        result = {
            "isRuntimeReady": True,
            "runtimeSummary": runtime_index["runtimeSummary"],
            "diagnostics": diagnostics
        }
    except Exception as ex:
        result = {
            "isRuntimeReady": False,
            "runtimeSummary": str(ex),
            "diagnostics": flow_diagnostics
        }

    json.dump(result, protocol_stdout, ensure_ascii=False)
    protocol_stdout.flush()


if __name__ == "__main__":
    main()
