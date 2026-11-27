import ast
import difflib
import json
import sys


def build_runtime_index():
    import vapoursynth as vs

    core = vs.core
    core_members = {}
    namespaces = {}

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
        "namespaces": namespaces
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
        return f"Unknown VapourSynth {kind} '{name}'. Did you mean '{suggestion[0]}'?"

    return f"Unknown VapourSynth {kind} '{name}'."


def locate_segment(node, source, segment):
    segment_text = ast.get_source_segment(source, node) or segment
    relative_index = segment_text.find(segment)
    start_column = node.col_offset + max(relative_index, 0) + 1
    end_column = start_column + len(segment)

    return {
        "startLine": node.lineno,
        "startColumn": start_column,
        "endLine": node.end_lineno or node.lineno,
        "endColumn": end_column
    }


def build_static_diagnostics(tree, source, runtime_index):
    diagnostics = []
    seen = set()

    assign_parents(tree)

    for node in ast.walk(tree):
        if not isinstance(node, ast.Attribute):
            continue

        if isinstance(getattr(node, "parent", None), ast.Attribute):
            continue

        chain = normalize_chain(extract_attribute_chain(node))
        if not chain or len(chain) < 2:
            continue

        if chain[0] != "core":
            continue

        root_member = chain[1]
        if len(chain) == 2:
            if root_member in runtime_index["coreMembers"]:
                continue

            location = locate_segment(node, source, root_member)
            diagnostic = {
                "code": "vapoursynth-core-member",
                "severity": "error",
                "message": build_unknown_member_message(
                    "core member",
                    root_member,
                    list(runtime_index["coreMembers"].keys())),
                "source": "vapoursynth",
                "relatedText": ast.get_source_segment(source, node) or root_member,
                **location
            }
        else:
            namespace = chain[1]
            function = chain[2]
            functions = runtime_index["namespaces"].get(namespace)

            if functions is None:
                location = locate_segment(node, source, namespace)
                diagnostic = {
                    "code": "vapoursynth-namespace",
                    "severity": "error",
                    "message": build_unknown_member_message(
                        "plugin namespace",
                        namespace,
                        list(runtime_index["namespaces"].keys())),
                    "source": "vapoursynth",
                    "relatedText": ast.get_source_segment(source, node) or namespace,
                    **location
                }
            elif function not in functions:
                location = locate_segment(node, source, function)
                diagnostic = {
                    "code": "vapoursynth-function",
                    "severity": "error",
                    "message": build_unknown_member_message(
                        "function",
                        function,
                        sorted(functions)),
                    "source": "vapoursynth",
                    "relatedText": ast.get_source_segment(source, node) or function,
                    **location
                }
            else:
                continue

        key = (
            diagnostic["code"],
            diagnostic["startLine"],
            diagnostic["startColumn"],
            diagnostic["endLine"],
            diagnostic["endColumn"],
            diagnostic["message"]
        )
        if key in seen:
            continue

        seen.add(key)
        diagnostics.append(diagnostic)

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
                if isinstance(target, ast.Name):
                    lhs = target.id
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

        used_after_def.update(_collect_load_names(stmt))

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


def main():
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
        json.dump(result, sys.stdout, ensure_ascii=False)
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

    json.dump(result, sys.stdout, ensure_ascii=False)


if __name__ == "__main__":
    main()
