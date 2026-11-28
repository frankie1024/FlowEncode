import importlib
import importlib.util
import inspect
import json
import os
from pathlib import Path
import sys


KNOWN_PYTHON_PACKAGES = (
    "vsjetpack",
    "vstools",
    "vsdeband",
    "vsdenoise",
    "vsaa",
    "vsscale",
    "vsdehalo",
    "vsmasktools",
    "vsrgtools",
    "vsdeinterlace",
    "vssource",
    "vskernels",
    "awsmfunc",
    "lvsfunc",
    "kagefunc",
    "vodesfunc",
    "fvsfunc",
)

VS_CONSTANT_PREFIXES = (
    "YUV",
    "RGB",
    "GRAY",
    "NONE",
    "INTEGER",
    "FLOAT",
    "MATRIX_",
    "TRANSFER_",
    "PRIMARIES_",
    "RANGE_",
    "FIELD_",
    "CHROMA_",
    "AUDIO_",
    "VIDEO",
    "AUDIO",
)


def format_annotation(annotation):
    if annotation is inspect.Signature.empty:
        return ""

    try:
        return inspect.formatannotation(annotation)
    except Exception:
        return str(annotation)


def build_parameter_descriptor(parameter):
    return {
        "name": parameter.name,
        "label": str(parameter),
        "documentation": ""
    }


def build_function_descriptor(namespace_name, plugin_name, function_name, function):
    signature = inspect.signature(function)
    return {
        "name": function_name,
        "qualifiedName": f"core.{namespace_name}.{function_name}",
        "signatureLabel": f"{function_name}{signature}",
        "returnType": format_annotation(signature.return_annotation),
        "parameters": [
            build_parameter_descriptor(parameter)
            for parameter in signature.parameters.values()
        ],
        "documentation": plugin_name or ""
    }


def collect_vs_constants(vs):
    constants = []
    for name in sorted(dir(vs)):
        if name.startswith("_"):
            continue
        if not name.isupper() and not any(name.startswith(prefix) for prefix in VS_CONSTANT_PREFIXES):
            continue
        try:
            value = getattr(vs, name)
        except Exception:
            continue
        if callable(value) or inspect.ismodule(value) or inspect.isclass(value):
            continue
        val_type = type(value).__name__
        detail = f"{val_type} = {value}"
        constants.append({
            "name": name,
            "kind": "enumMember" if "Enum" in val_type or "Preset" in val_type or "Matrix" in val_type or "Transfer" in val_type or "Primaries" in val_type or "Range" in val_type or "Field" in val_type or "Chroma" in val_type else "constant",
            "detail": detail,
            "documentation": f"vapoursynth.{name} ({detail})"
        })
    return constants


def collect_video_node_members(vs):
    members = []
    node_cls = getattr(vs, "VideoNode", None)
    if node_cls is None:
        return members

    for name in sorted(dir(node_cls)):
        if name.startswith("_"):
            continue
        try:
            attr = getattr(node_cls, name)
        except Exception:
            continue
        is_callable = callable(attr)
        doc = (inspect.getdoc(attr) or "").splitlines()
        doc_summary = doc[0] if doc else f"VideoNode.{name}"
        detail = "method"
        if is_callable:
            try:
                sig = inspect.signature(attr)
                detail = f"{name}{sig}"
            except Exception:
                detail = f"{name}(...)"
        else:
            detail = f"VideoNode.{name} property"
        members.append({
            "name": name,
            "kind": "method" if is_callable else "property",
            "detail": detail,
            "documentation": doc_summary
        })
    return members


def probe_installed_packages():
    installed = []
    for pkg in KNOWN_PYTHON_PACKAGES:
        try:
            if importlib.util.find_spec(pkg) is not None:
                installed.append(pkg)
        except Exception:
            continue
    return installed


def probe_template_symbols():
    path = Path(__file__).resolve().parent.parent / "filter-templates.json"
    with path.open(encoding="utf-8") as template_file:
        templates = json.load(template_file)

    symbols = {
        requirement
        for item in templates
        for requirement in item.get("requires", [])
        if not requirement.startswith("core.")
    }
    symbols.update(f"{item['module']}.{item['symbol']}" for item in templates if item.get("symbol"))
    symbols.update(f"vssource.{name}" for name in ("BestSource", "LSMAS", "FFMS2"))
    available = []
    for qualified in sorted(symbols):
        module, _, name = qualified.rpartition(".")
        try:
            if callable(getattr(importlib.import_module(module), name)):
                available.append(qualified)
        except Exception:
            continue
    return available


def build_runtime_snapshot():
    import vapoursynth as vs

    core = vs.core
    namespaces = []
    core_members = []

    for name in sorted(dir(core)):
        if name.startswith("_"):
            continue

        try:
            item = getattr(core, name)
        except Exception:
            continue

        item_type = type(item).__name__
        if item_type == "Plugin":
            try:
                plugin_namespace = getattr(item, "namespace", name)
            except Exception:
                plugin_namespace = name

            try:
                plugin_identifier = getattr(item, "identifier", "")
            except Exception:
                plugin_identifier = ""

            try:
                plugin_display_name = getattr(item, "name", plugin_namespace)
            except Exception:
                plugin_display_name = plugin_namespace

            functions = []
            for function_name in sorted(dir(item)):
                if function_name.startswith("_"):
                    continue

                try:
                    function = getattr(item, function_name)
                except Exception:
                    continue

                if not callable(function):
                    continue

                try:
                    descriptor = build_function_descriptor(
                        plugin_namespace,
                        plugin_display_name,
                        function_name,
                        function)
                except Exception:
                    descriptor = {
                        "name": function_name,
                        "qualifiedName": f"core.{plugin_namespace}.{function_name}",
                        "signatureLabel": function_name,
                        "returnType": "",
                        "parameters": [],
                        "documentation": plugin_display_name or ""
                    }

                functions.append(descriptor)

            namespaces.append({
                "name": plugin_namespace,
                "identifier": plugin_identifier,
                "displayName": plugin_display_name,
                "functions": functions
            })
            core_members.append({
                "name": plugin_namespace,
                "kind": "namespace",
                "detail": plugin_identifier or plugin_display_name,
                "documentation": plugin_display_name or ""
            })
            continue

        if callable(item):
            core_members.append({
                "name": name,
                "kind": "method",
                "detail": item_type,
                "documentation": ""
            })
            continue

        core_members.append({
            "name": name,
            "kind": "property",
            "detail": item_type,
            "documentation": ""
        })

    core_version = getattr(vs, "__version__", None)
    if isinstance(core_version, (tuple, list)) and core_version:
        core_version_label = ".".join(str(part) for part in core_version)
    else:
        core_version_label = str(core_version or "unknown")

    return {
        "isRuntimeReady": True,
        "runtimeSummary": f"VapourSynth {core_version_label} / {len(namespaces)} plugins",
        "coreMembers": core_members,
        "namespaces": namespaces,
        "vsConstants": collect_vs_constants(vs),
        "videoNodeMembers": collect_video_node_members(vs),
        "installedPackages": probe_installed_packages(),
        "templateSymbols": probe_template_symbols(),
    }


def _acquire_protocol_stdout():
    try:
        saved_fd = os.dup(1)
        os.dup2(2, 1)
        sys.stdout = sys.stderr
        return os.fdopen(saved_fd, "w", encoding="utf-8", errors="backslashreplace")
    except Exception:
        return sys.stdout


def main():
    protocol_stdout = _acquire_protocol_stdout()
    try:
        payload = build_runtime_snapshot()
    except Exception as ex:
        payload = {
            "isRuntimeReady": False,
            "runtimeSummary": str(ex),
            "coreMembers": [],
            "namespaces": [],
            "vsConstants": [],
            "videoNodeMembers": [],
            "installedPackages": []
        }

    json.dump(payload, protocol_stdout, ensure_ascii=False)
    protocol_stdout.flush()


if __name__ == "__main__":
    main()
