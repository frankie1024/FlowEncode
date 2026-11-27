const editorContainer = document.getElementById("editorContainer");
const colorScheme = window.matchMedia("(prefers-color-scheme: dark)");
const diagnosticsOwner = "vapoursynth-workspace";

let editorInstance = null;
let editorModel = null;
let suppressModelEvents = false;
let bufferTimer = 0;
let cursorTimer = 0;
let pendingDocumentText = "";
let pendingDocumentFilePath = "";
let currentDocumentPath = "";
let currentDocumentBinding = null;
let hostTheme = "";
let languageFeatures = createEmptyLanguageFeatures();
let languageFeatureIndex = buildLanguageFeatureIndex(languageFeatures);
let nextLanguageRequestId = 1;
const pendingLanguageRequests = new Map();

function createEmptyLanguageFeatures() {
    return {
        isRuntimeReady: false,
        runtimeSummary: "",
        coreMembers: [],
        namespaces: [],
        vsConstants: [],
        videoNodeMembers: [],
        installedPackages: []
    };
}

function postMessage(payload) {
    if (window.chrome?.webview) {
        window.chrome.webview.postMessage({
            ...payload,
            binding: currentDocumentBinding
        });
    }
}

function normalizeText(text) {
    return typeof text === "string" ? text : "";
}

function normalizeArray(value) {
    return Array.isArray(value) ? value : [];
}

function normalizeDocumentBinding(value) {
    if (!value
        || typeof value.paneId !== "string"
        || value.paneId.length === 0
        || typeof value.tabId !== "string"
        || value.tabId.length === 0
        || !Number.isSafeInteger(value.loadGeneration)
        || value.loadGeneration <= 0) {
        return null;
    }

    return Object.freeze({
        paneId: value.paneId,
        tabId: value.tabId,
        loadGeneration: value.loadGeneration
    });
}

function createCanceledError(message) {
    const error = new Error(message);
    error.name = "Canceled";
    return error;
}

function cancelPendingLanguageRequests(message) {
    for (const [requestId, pendingRequest] of pendingLanguageRequests.entries()) {
        pendingLanguageRequests.delete(requestId);
        pendingRequest.reject(createCanceledError(message));
    }
}

function requestLanguageFeature(method, payload, cancellationToken) {
    if (!window.chrome?.webview) {
        return Promise.reject(new Error("WebView host is unavailable."));
    }

    if (cancellationToken?.isCancellationRequested) {
        return Promise.reject(createCanceledError("Language request cancelled."));
    }

    const requestId = String(nextLanguageRequestId++);

    return new Promise((resolve, reject) => {
        let cancellationRegistration = null;

        const complete = (callback) => {
            if (cancellationRegistration?.dispose) {
                cancellationRegistration.dispose();
            }

            callback();
        };

        pendingLanguageRequests.set(requestId, {
            resolve(result) {
                complete(() => resolve(result));
            },
            reject(error) {
                complete(() => reject(error));
            }
        });

        if (typeof cancellationToken?.onCancellationRequested === "function") {
            cancellationRegistration = cancellationToken.onCancellationRequested(() => {
                const pendingRequest = pendingLanguageRequests.get(requestId);
                if (!pendingRequest) {
                    return;
                }

                pendingLanguageRequests.delete(requestId);
                pendingRequest.reject(createCanceledError("Language request cancelled."));
            });
        }

        postMessage({
            type: "languageRequest",
            requestId,
            method,
            ...payload
        });
    });
}

function resolveLanguageRequest(payload) {
    const requestId = normalizeText(payload?.requestId);
    if (!requestId) {
        return;
    }

    const pendingRequest = pendingLanguageRequests.get(requestId);
    if (!pendingRequest) {
        return;
    }

    pendingLanguageRequests.delete(requestId);

    if (payload?.success === false) {
        pendingRequest.reject(new Error(normalizeText(payload?.error) || "Language request failed."));
        return;
    }

    pendingRequest.resolve(payload?.result ?? null);
}

function buildLanguageRequestPayload(model, position, extraPayload) {
    return {
        filePath: currentDocumentPath,
        text: model.getValue(monaco.editor.EndOfLinePreference.LF),
        line: position.lineNumber,
        column: position.column,
        ...(extraPayload ?? {})
    };
}

function mapPythonCompletionKind(kind) {
    switch ((kind ?? "").toLowerCase()) {
        case "method":
            return monaco.languages.CompletionItemKind.Method;
        case "function":
            return monaco.languages.CompletionItemKind.Function;
        case "constructor":
            return monaco.languages.CompletionItemKind.Constructor;
        case "field":
            return monaco.languages.CompletionItemKind.Field;
        case "variable":
            return monaco.languages.CompletionItemKind.Variable;
        case "class":
            return monaco.languages.CompletionItemKind.Class;
        case "interface":
            return monaco.languages.CompletionItemKind.Interface;
        case "module":
            return monaco.languages.CompletionItemKind.Module;
        case "property":
            return monaco.languages.CompletionItemKind.Property;
        case "unit":
            return monaco.languages.CompletionItemKind.Unit;
        case "value":
            return monaco.languages.CompletionItemKind.Value;
        case "enum":
            return monaco.languages.CompletionItemKind.Enum;
        case "keyword":
            return monaco.languages.CompletionItemKind.Keyword;
        case "snippet":
            return monaco.languages.CompletionItemKind.Snippet;
        case "color":
            return monaco.languages.CompletionItemKind.Color;
        case "file":
            return monaco.languages.CompletionItemKind.File;
        case "reference":
            return monaco.languages.CompletionItemKind.Reference;
        case "folder":
            return monaco.languages.CompletionItemKind.Folder;
        case "enummember":
            return monaco.languages.CompletionItemKind.EnumMember;
        case "constant":
            return monaco.languages.CompletionItemKind.Constant;
        case "struct":
            return monaco.languages.CompletionItemKind.Struct;
        case "event":
            return monaco.languages.CompletionItemKind.Event;
        case "operator":
            return monaco.languages.CompletionItemKind.Operator;
        case "typeparam":
            return monaco.languages.CompletionItemKind.TypeParameter;
        default:
            return monaco.languages.CompletionItemKind.Text;
    }
}

function mapLspCompletionItems(payload, range) {
    return normalizeArray(payload)
        .filter((item) => item && typeof item.label === "string")
        .map((item) => {
            const completionItem = {
                label: item.label,
                kind: mapPythonCompletionKind(item.kind),
                detail: normalizeText(item.detail),
                documentation: normalizeText(item.documentation),
                insertText: normalizeText(item.insertText) || item.label,
                filterText: normalizeText(item.filterText) || item.label,
                sortText: normalizeText(item.sortText) || item.label,
                range
            };

            if (item.isSnippet === true) {
                completionItem.insertTextRules = monaco.languages.CompletionItemInsertTextRule.InsertAsSnippet;
            }

            return completionItem;
        });
}

function buildCompletionKey(item) {
    return [
        normalizeText(item.label),
        String(item.kind ?? ""),
        normalizeText(item.insertText),
        normalizeText(item.detail)
    ].join("|");
}

function mergeCompletionSuggestions(...groups) {
    const merged = [];
    const seen = new Set();

    for (const group of groups) {
        for (const item of normalizeArray(group)) {
            const key = buildCompletionKey(item);
            if (seen.has(key)) {
                continue;
            }

            seen.add(key);
            merged.push(item);
        }
    }

    return merged;
}

function normalizeLanguageFeatures(payload) {
    const coreMembers = normalizeArray(payload?.coreMembers)
        .filter((item) => item && typeof item.name === "string")
        .map((item) => ({
            name: item.name,
            kind: item.kind ?? "property",
            detail: item.detail ?? "",
            documentation: item.documentation ?? ""
        }));

    const namespaces = normalizeArray(payload?.namespaces)
        .filter((item) => item && typeof item.name === "string")
        .map((item) => ({
            name: item.name,
            identifier: item.identifier ?? "",
            displayName: item.displayName ?? item.name,
            functions: normalizeArray(item.functions)
                .filter((functionItem) => functionItem && typeof functionItem.name === "string")
                .map((functionItem) => ({
                    name: functionItem.name,
                    qualifiedName: functionItem.qualifiedName ?? `core.${item.name}.${functionItem.name}`,
                    signatureLabel: functionItem.signatureLabel ?? functionItem.name,
                    returnType: functionItem.returnType ?? "",
                    parameters: normalizeArray(functionItem.parameters)
                        .filter((parameter) => parameter && typeof parameter.name === "string")
                        .map((parameter) => ({
                            name: parameter.name,
                            label: parameter.label ?? parameter.name,
                            documentation: parameter.documentation ?? ""
                        })),
                    documentation: functionItem.documentation ?? item.displayName ?? ""
                }))
        }));

    const vsConstants = normalizeArray(payload?.vsConstants)
        .filter((item) => item && typeof item.name === "string")
        .map((item) => ({
            name: item.name,
            kind: item.kind ?? "constant",
            detail: item.detail ?? "",
            documentation: item.documentation ?? ""
        }));

    const videoNodeMembers = normalizeArray(payload?.videoNodeMembers)
        .filter((item) => item && typeof item.name === "string")
        .map((item) => ({
            name: item.name,
            kind: item.kind ?? "property",
            detail: item.detail ?? "",
            documentation: item.documentation ?? ""
        }));

    const installedPackages = normalizeArray(payload?.installedPackages)
        .filter((item) => typeof item === "string" && item.length > 0);

    return {
        isRuntimeReady: payload?.isRuntimeReady === true,
        runtimeSummary: payload?.runtimeSummary ?? "",
        coreMembers,
        namespaces,
        vsConstants,
        videoNodeMembers,
        installedPackages
    };
}

function normalizeQualifiedName(value) {
    return typeof value === "string"
        ? value.replace(/^vs\./, "")
        : "";
}

function buildLanguageFeatureIndex(features) {
    const namespaceMap = new Map();
    const functionMap = new Map();
    const vsConstantMap = new Map();
    const videoNodeMemberMap = new Map();
    const installedPackages = new Set(normalizeArray(features.installedPackages));

    for (const namespaceItem of features.namespaces) {
        namespaceMap.set(namespaceItem.name, namespaceItem);

        for (const functionItem of namespaceItem.functions) {
            functionMap.set(
                normalizeQualifiedName(functionItem.qualifiedName),
                {
                    ...functionItem,
                    namespaceName: namespaceItem.name,
                    namespaceDisplayName: namespaceItem.displayName
                });
        }
    }

    for (const constantItem of normalizeArray(features.vsConstants)) {
        vsConstantMap.set(constantItem.name, constantItem);
    }

    for (const memberItem of normalizeArray(features.videoNodeMembers)) {
        videoNodeMemberMap.set(memberItem.name, memberItem);
    }

    return {
        rootMembers: features.coreMembers,
        namespaceMap,
        functionMap,
        vsConstants: normalizeArray(features.vsConstants),
        vsConstantMap,
        videoNodeMembers: normalizeArray(features.videoNodeMembers),
        videoNodeMemberMap,
        installedPackages
    };
}

function loadLanguageFeatures(payload) {
    languageFeatures = normalizeLanguageFeatures(payload);
    languageFeatureIndex = buildLanguageFeatureIndex(languageFeatures);
}

function mapMarkerSeverity(severity) {
    switch ((severity ?? "").toLowerCase()) {
        case "warning":
            return monaco.MarkerSeverity.Warning;
        case "info":
            return monaco.MarkerSeverity.Info;
        case "hint":
            return monaco.MarkerSeverity.Hint;
        default:
            return monaco.MarkerSeverity.Error;
    }
}

let latestDiagnostics = [];

function applyDiagnostics(payload) {
    if (!editorModel || typeof monaco === "undefined") {
        return;
    }

    const diagnostics = normalizeArray(payload?.diagnostics);
    latestDiagnostics = diagnostics;
    const markers = diagnostics.map((diagnostic) => ({
        severity: mapMarkerSeverity(diagnostic.severity),
        code: diagnostic.code ?? "",
        source: diagnostic.source ?? "vapoursynth",
        message: diagnostic.message ?? "Unknown diagnostic.",
        startLineNumber: Math.max(1, diagnostic.startLine ?? 1),
        startColumn: Math.max(1, diagnostic.startColumn ?? 1),
        endLineNumber: Math.max(1, diagnostic.endLine ?? diagnostic.startLine ?? 1),
        endColumn: Math.max(1, diagnostic.endColumn ?? diagnostic.startColumn ?? 1),
        relatedInformation: diagnostic.relatedText
            ? [{
                resource: editorModel.uri,
                startLineNumber: Math.max(1, diagnostic.startLine ?? 1),
                startColumn: Math.max(1, diagnostic.startColumn ?? 1),
                endLineNumber: Math.max(1, diagnostic.endLine ?? diagnostic.startLine ?? 1),
                endColumn: Math.max(1, diagnostic.endColumn ?? diagnostic.startColumn ?? 1),
                message: diagnostic.relatedText
            }]
            : []
    }));

    monaco.editor.setModelMarkers(editorModel, diagnosticsOwner, markers);
}

function buildState() {
    if (!editorInstance || !editorModel || typeof monaco === "undefined") {
        return {
            text: "",
            line: 1,
            column: 1,
            lineCount: 1,
            charCount: 0,
            binding: currentDocumentBinding
        };
    }

    const position = editorInstance.getPosition() ?? { lineNumber: 1, column: 1 };
    const text = editorModel.getValue(monaco.editor.EndOfLinePreference.LF);

    return {
        text,
        line: position.lineNumber,
        column: position.column,
        lineCount: editorModel.getLineCount(),
        charCount: text.length,
        binding: currentDocumentBinding
    };
}

function queueBufferBroadcast() {
    window.clearTimeout(bufferTimer);
    bufferTimer = window.setTimeout(() => {
        postMessage({
            type: "bufferChanged",
            ...buildState()
        });
    }, 40);
}

function queueCursorBroadcast() {
    window.clearTimeout(cursorTimer);
    cursorTimer = window.setTimeout(() => {
        const state = buildState();
        postMessage({
            type: "cursorChanged",
            line: state.line,
            column: state.column,
            lineCount: state.lineCount,
            charCount: state.charCount
        });
    }, 20);
}

function focusEditor() {
    editorInstance?.focus();
}

function captureStateJson() {
    return JSON.stringify(buildState());
}

function clearDiagnostics() {
    if (!editorModel || typeof monaco === "undefined") {
        return;
    }

    monaco.editor.setModelMarkers(editorModel, diagnosticsOwner, []);
}

function loadDocument(payload, options) {
    const shouldBroadcastState = options?.broadcastState !== false;
    const nextBinding = normalizeDocumentBinding(payload?.binding);
    if (payload?.binding != null && !nextBinding) {
        return { loaded: false, binding: null };
    }

    if (!editorModel || !editorInstance || typeof monaco === "undefined") {
        return { loaded: false, binding: null };
    }

    window.clearTimeout(bufferTimer);
    window.clearTimeout(cursorTimer);
    pendingDocumentText = normalizeText(payload?.text);
    pendingDocumentFilePath = normalizeText(payload?.filePath);
    cancelPendingLanguageRequests("Document changed.");

    suppressModelEvents = true;
    try {
        editorModel.setValue(pendingDocumentText);
        editorModel.setEOL(monaco.editor.EndOfLineSequence.LF);
        editorInstance.setScrollPosition({ scrollTop: 0, scrollLeft: 0 });
        editorInstance.setPosition({ lineNumber: 1, column: 1 });
        editorInstance.revealPositionInCenter({ lineNumber: 1, column: 1 });
    } finally {
        suppressModelEvents = false;
    }

    currentDocumentPath = pendingDocumentFilePath;
    currentDocumentBinding = nextBinding;

    clearDiagnostics();
    if (shouldBroadcastState) {
        queueBufferBroadcast();
        queueCursorBroadcast();
    }
    focusEditor();
    return { loaded: true, binding: currentDocumentBinding };
}

function buildInsertionRange(target) {
    if (!editorInstance || !editorModel || typeof monaco === "undefined") {
        return null;
    }

    if (target === "newLine") {
        const position = editorInstance.getPosition() ?? { lineNumber: 1, column: 1 };
        const lineNumber = Math.max(1, position.lineNumber);
        const lineMaxColumn = editorModel.getLineMaxColumn(lineNumber);
        return new monaco.Range(lineNumber, lineMaxColumn, lineNumber, lineMaxColumn);
    }

    return editorInstance.getSelection()
        ?? (() => {
            const position = editorInstance.getPosition() ?? { lineNumber: 1, column: 1 };
            return new monaco.Range(
                position.lineNumber,
                position.column,
                position.lineNumber,
                position.column);
        })();
}

function insertText(payload) {
    if (!editorInstance || !editorModel || typeof monaco === "undefined") {
        return false;
    }

    const text = normalizeText(payload?.text);
    if (!text) {
        return false;
    }

    const target = normalizeText(payload?.target);
    const range = buildInsertionRange(target);
    if (!range) {
        return false;
    }

    let insertValue = text;
    if (target === "newLine" && editorModel.getValueLength() > 0) {
        insertValue = "\n" + insertValue;
    }

    const beforeValue = editorModel.getValue();
    const applied = editorInstance.executeEdits("vsWorkspaceHost", [{
        range,
        text: insertValue,
        forceMoveMarkers: true
    }]);

    if (!applied || editorModel.getValue() === beforeValue) {
        return false;
    }

    queueBufferBroadcast();
    queueCursorBroadcast();
    focusEditor();
    return true;
}

function stripSnippetPlaceholders(snippet) {
    return snippet
        .replace(/\$\{[0-9]+:([^}]*)\}/g, "$1")
        .replace(/\$\{[0-9]+\}/g, "")
        .replace(/\$[0-9]+/g, "");
}

function insertSnippet(payload) {
    if (!editorInstance || !editorModel || typeof monaco === "undefined") {
        return false;
    }

    const snippet = normalizeText(payload?.snippet);
    if (!snippet) {
        return false;
    }

    const target = normalizeText(payload?.target);
    const range = buildInsertionRange(target);
    if (!range) {
        return false;
    }

    const snippetText = target === "newLine" && editorModel.getValueLength() > 0
        ? "\n" + snippet
        : snippet;

    editorInstance.setSelection(range);
    editorInstance.focus();

    const snippetController = editorInstance.getContribution?.("snippetController2");
    if (snippetController && typeof snippetController.insert === "function") {
        const beforeValue = editorModel.getValue();
        snippetController.insert(snippetText);
        if (editorModel.getValue() !== beforeValue) {
            queueBufferBroadcast();
            queueCursorBroadcast();
            focusEditor();
            return true;
        }
    }

    return insertText({
        text: stripSnippetPlaceholders(snippetText),
        target: "cursor"
    });
}

async function runAction(actionId) {
    if (!editorInstance) {
        return;
    }

    const action = editorInstance.getAction(actionId);
    if (action) {
        await action.run();
    }
}

async function executeCommand(command) {
    if (!editorInstance) {
        return;
    }

    switch (command) {
        case "undo":
            editorInstance.trigger("vsWorkspaceHost", "undo", null);
            break;
        case "redo":
            editorInstance.trigger("vsWorkspaceHost", "redo", null);
            break;
        case "find":
            await runAction("actions.find");
            break;
        case "replace":
            await runAction("editor.action.startFindReplaceAction");
            break;
        case "goto":
            await runAction("editor.action.gotoLine");
            break;
        case "suggest":
            editorInstance.focus();
            await runAction("editor.action.triggerSuggest");
            break;
        case "rename":
            editorInstance.focus();
            await runAction("editor.action.rename");
            break;
        case "quickFix":
            editorInstance.focus();
            await runAction("editor.action.quickFix");
            break;
        case "focus":
            focusEditor();
            break;
    }

    queueCursorBroadcast();
}

function dispatchHostCommand(command) {
    postMessage({
        type: "hostCommand",
        command
    });
}

function resolveEditorThemeName() {
    if (hostTheme === "dark") {
        return "dark";
    }

    if (hostTheme === "light") {
        return "light";
    }

    return colorScheme.matches ? "dark" : "light";
}

function updateDocumentTheme() {
    const themeName = resolveEditorThemeName();
    document.documentElement.dataset.theme = themeName;
    return themeName;
}

function defineThemes() {
    monaco.editor.defineTheme("vapoursynth-workspace-light", {
        base: "vs",
        inherit: true,
        rules: [
            { token: "keyword", foreground: "8f3a07", fontStyle: "bold" },
            { token: "string", foreground: "1b6a3a" },
            { token: "comment", foreground: "897565", fontStyle: "italic" },
            { token: "number", foreground: "8c5a18" }
        ],
        colors: {
            "editor.background": "#00000000",
            "editor.foreground": "#221912",
            "editorLineNumber.foreground": "#8e7768",
            "editorLineNumber.activeForeground": "#3d2f23",
            "editorCursor.foreground": "#b76124",
            "editor.selectionBackground": "#d98c593a",
            "editor.inactiveSelectionBackground": "#d98c5922",
            "editor.lineHighlightBackground": "#b7612416",
            "editor.lineHighlightBorder": "#00000000",
            "editorIndentGuide.background1": "#d8c9ba",
            "editorIndentGuide.activeBackground1": "#b08e73",
            "editorBracketMatch.background": "#b7612418",
            "editorBracketMatch.border": "#d07c42",
            "editorWidget.background": "#f6efe7",
            "editorWidget.border": "#d7c5b2",
            "editorSuggestWidget.background": "#f6efe7",
            "editorSuggestWidget.border": "#d7c5b2",
            "editorSuggestWidget.selectedBackground": "#e8d5c4",
            "editor.findMatchBackground": "#efb77a70",
            "editor.findMatchHighlightBackground": "#efb77a33",
            "editor.foldBackground": "#b7612410",
            "scrollbarSlider.background": "#9c7b5b55",
            "scrollbarSlider.hoverBackground": "#9c7b5b77",
            "scrollbarSlider.activeBackground": "#9c7b5b99"
        }
    });

    monaco.editor.defineTheme("vapoursynth-workspace-dark", {
        base: "vs-dark",
        inherit: true,
        rules: [
            { token: "keyword", foreground: "f2925f", fontStyle: "bold" },
            { token: "string", foreground: "8ed08a" },
            { token: "comment", foreground: "7d919f", fontStyle: "italic" },
            { token: "number", foreground: "e1b96f" }
        ],
        colors: {
            "editor.background": "#00000000",
            "editor.foreground": "#e6e8ea",
            "editorLineNumber.foreground": "#6e818d",
            "editorLineNumber.activeForeground": "#dde4e8",
            "editorCursor.foreground": "#ef8451",
            "editor.selectionBackground": "#ef845140",
            "editor.inactiveSelectionBackground": "#ef845126",
            "editor.lineHighlightBackground": "#ef845116",
            "editor.lineHighlightBorder": "#00000000",
            "editorIndentGuide.background1": "#30414d",
            "editorIndentGuide.activeBackground1": "#5c7381",
            "editorBracketMatch.background": "#ef845118",
            "editorBracketMatch.border": "#ef8451",
            "editorWidget.background": "#202a31",
            "editorWidget.border": "#30414d",
            "editorSuggestWidget.background": "#202a31",
            "editorSuggestWidget.border": "#30414d",
            "editorSuggestWidget.selectedBackground": "#2b3942",
            "editor.findMatchBackground": "#f3b17255",
            "editor.findMatchHighlightBackground": "#f3b1722d",
            "editor.foldBackground": "#ef845110",
            "scrollbarSlider.background": "#6d879655",
            "scrollbarSlider.hoverBackground": "#6d879677",
            "scrollbarSlider.activeBackground": "#6d879699"
        }
    });
}

function applyTheme() {
    const themeName = updateDocumentTheme();

    if (typeof monaco === "undefined") {
        return;
    }

    monaco.editor.setTheme(
        themeName === "dark"
            ? "vapoursynth-workspace-dark"
            : "vapoursynth-workspace-light");
}

function applyHostTheme(payload) {
    const requestedTheme = normalizeText(payload?.theme).toLowerCase();
    hostTheme = requestedTheme === "dark" || requestedTheme === "light"
        ? requestedTheme
        : "";

    applyTheme();
}

function registerHostCommands() {
    editorInstance.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyN, () => {
        dispatchHostCommand("new");
    });
    editorInstance.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyO, () => {
        dispatchHostCommand("open");
    });
    editorInstance.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyS, () => {
        dispatchHostCommand("save");
    });
    editorInstance.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyMod.Shift | monaco.KeyCode.KeyS, () => {
        dispatchHostCommand("saveAs");
    });
    editorInstance.addCommand(monaco.KeyCode.F5, () => {
        dispatchHostCommand("preview");
    });
    editorInstance.addCommand(monaco.KeyCode.F9, () => {
        dispatchHostCommand("encode");
    });

    editorInstance.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyF, () => {
        void executeCommand("find");
    });
    editorInstance.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyH, () => {
        void executeCommand("replace");
    });
    editorInstance.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyG, () => {
        void executeCommand("goto");
    });
    editorInstance.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.Space, () => {
        void executeCommand("suggest");
    });
}

function matchesPrefix(value, prefix) {
    return prefix.length === 0
        || value.toLowerCase().startsWith(prefix.toLowerCase());
}

function getWordRange(position) {
    const word = editorModel?.getWordUntilPosition(position);
    if (!word) {
        return {
            startLineNumber: position.lineNumber,
            endLineNumber: position.lineNumber,
            startColumn: position.column,
            endColumn: position.column
        };
    }

    return {
        startLineNumber: position.lineNumber,
        endLineNumber: position.lineNumber,
        startColumn: word.startColumn,
        endColumn: word.endColumn
    };
}

const NON_CLIP_VARIABLES = new Set(["core", "vs", "os", "sys", "math", "Path", "path"]);

function collectModelClipVariables(model) {
    const vars = new Set();
    const importedNames = new Set();
    const importedModules = new Map(); // moduleName -> alias
    const importedSymbols = new Map(); // originalSymbol -> alias
    let hasCore = false;
    let lastImportLine = 0;
    let outputVar = "";
    let lastAssignedVar = "";
    const lineCount = model.getLineCount();

    for (let lineNum = 1; lineNum <= lineCount; lineNum++) {
        const line = model.getLineContent(lineNum);
        const trimmed = line.trim();
        if (!trimmed || trimmed.startsWith("#")) {
            continue;
        }

        const importMatch = /^import\s+([A-Za-z_][A-Za-z0-9_\.]*)(?:\s+as\s+([A-Za-z_][A-Za-z0-9_]*))?/.exec(trimmed);
        if (importMatch) {
            const mod = importMatch[1];
            const alias = importMatch[2] || mod.split(".")[0];
            importedNames.add(alias);
            importedModules.set(mod, alias);
            lastImportLine = lineNum;
            continue;
        }

        const fromMatch = /^from\s+([A-Za-z_][A-Za-z0-9_\.]*)\s+import\s+(.+)$/.exec(trimmed);
        if (fromMatch) {
            const mod = fromMatch[1];
            lastImportLine = lineNum;
            for (const part of fromMatch[2].split(",")) {
                const entry = part.trim();
                const aliasMatch = /^([A-Za-z_][A-Za-z0-9_]*)(?:\s+as\s+([A-Za-z_][A-Za-z0-9_]*))?/.exec(entry);
                if (aliasMatch) {
                    const orig = aliasMatch[1];
                    const alias = aliasMatch[2] || orig;
                    importedNames.add(alias);
                    importedSymbols.set(orig, alias);
                    if (mod === "vapoursynth" && alias === "core") {
                        hasCore = true;
                    }
                }
            }
            continue;
        }

        if (/^core\s*=/.test(trimmed)) {
            hasCore = true;
            continue;
        }

        const outMatch = /^([A-Za-z_][A-Za-z0-9_]*)\.set_output\s*\(/.exec(trimmed);
        if (outMatch) {
            outputVar = outMatch[1];
            vars.add(outMatch[1]);
            continue;
        }

        const assignMatch = /^([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.+)$/.exec(line);
        if (assignMatch && !NON_CLIP_VARIABLES.has(assignMatch[1]) && !importedNames.has(assignMatch[1])) {
            vars.add(assignMatch[1]);
            lastAssignedVar = assignMatch[1];
        }
    }

    return {
        vars,
        importedNames,
        importedModules,
        importedSymbols,
        hasCore,
        lastImportLine,
        activeVar: outputVar || lastAssignedVar || "clip"
    };
}

function parseVapourSynthChain(token, clipInfo) {
    if (!token) {
        return null;
    }

    const segments = token.split(".");
    if (segments[0] === "vs") {
        if (segments.length >= 2 && segments[1] === "core") {
            return {
                mode: "core",
                raw: token,
                normalizedSegments: ["core", ...segments.slice(2)]
            };
        }
        return {
            mode: "vs",
            raw: token,
            normalizedSegments: segments
        };
    }

    if (segments[0] === "core") {
        return {
            mode: "core",
            raw: token,
            normalizedSegments: segments
        };
    }

    if (clipInfo?.vars?.has(segments[0]) && !clipInfo.importedNames?.has(segments[0])) {
        return {
            mode: "clip",
            rootVar: segments[0],
            raw: token,
            normalizedSegments: segments
        };
    }

    return null;
}

function getChainTokenBeforeCursor(model, position) {
    const lineText = model.getLineContent(position.lineNumber).slice(0, position.column - 1);
    let index = lineText.length - 1;

    while (index >= 0 && /[\w.]/.test(lineText[index])) {
        index -= 1;
    }

    return lineText.slice(index + 1);
}

function getChainTokenAtPosition(model, position) {
    const lineText = model.getLineContent(position.lineNumber);
    let start = Math.max(0, position.column - 1);
    let end = start;
    while (start > 0 && /[\w.]/.test(lineText[start - 1])) {
        start -= 1;
    }
    while (end < lineText.length && /[\w]/.test(lineText[end])) {
        end += 1;
    }
    return {
        token: lineText.slice(start, end),
        startColumn: start + 1,
        endColumn: end + 1
    };
}

function resolveVapourSynthFunctionFromChain(chainText, clipInfo) {
    const parsed = parseVapourSynthChain(chainText, clipInfo);
    if (!parsed) {
        return null;
    }

    const segs = parsed.normalizedSegments;
    if ((parsed.mode === "core" || parsed.mode === "clip") && segs.length >= 3) {
        const ns = segs[1];
        const fn = segs[2];
        const descriptor = languageFeatureIndex.functionMap.get(`core.${ns}.${fn}`);
        if (descriptor) {
            return {
                descriptor,
                isChained: parsed.mode === "clip"
            };
        }
    }

    return null;
}

function findEnclosingCallContext(model, position, clipInfo) {
    const startLine = Math.max(1, position.lineNumber - 8);
    let text = "";
    for (let l = startLine; l < position.lineNumber; l++) {
        text += model.getLineContent(l) + "\n";
    }
    text += model.getLineContent(position.lineNumber).slice(0, position.column - 1);

    let parenDepth = 0;
    let bracketDepth = 0;
    let braceDepth = 0;
    let openParenIdx = -1;
    let inQuote = null;

    for (let i = text.length - 1; i >= 0; i--) {
        const ch = text[i];
        if (inQuote) {
            if (ch === inQuote && text[i - 1] !== "\\") {
                inQuote = null;
            }
            continue;
        }
        if (ch === '"' || ch === "'") {
            inQuote = ch;
            continue;
        }
        if (ch === ")") parenDepth++;
        else if (ch === "(") {
            if (parenDepth === 0) {
                openParenIdx = i;
                break;
            }
            parenDepth--;
        } else if (ch === "]") bracketDepth++;
        else if (ch === "[") bracketDepth = Math.max(0, bracketDepth - 1);
        else if (ch === "}") braceDepth++;
        else if (ch === "{") braceDepth = Math.max(0, braceDepth - 1);
    }

    if (openParenIdx < 0) {
        return null;
    }

    let calleeEnd = openParenIdx;
    while (calleeEnd > 0 && /\s/.test(text[calleeEnd - 1])) {
        calleeEnd--;
    }
    let calleeStart = calleeEnd;
    while (calleeStart > 0 && /[\w.]/.test(text[calleeStart - 1])) {
        calleeStart--;
    }
    const calleeChain = text.slice(calleeStart, calleeEnd);
    const argsText = text.slice(openParenIdx + 1);

    let argIndex = 0;
    let dParen = 0;
    let dBrack = 0;
    let dBrace = 0;
    let q = null;
    const argSegments = [];
    let segStart = 0;

    for (let i = 0; i < argsText.length; i++) {
        const ch = argsText[i];
        if (q) {
            if (ch === "\\" && i + 1 < argsText.length) {
                i++;
                continue;
            }
            if (ch === q) q = null;
            continue;
        }
        if (ch === '"' || ch === "'") {
            q = ch;
        } else if (ch === "(") dParen++;
        else if (ch === ")") dParen = Math.max(0, dParen - 1);
        else if (ch === "[") dBrack++;
        else if (ch === "]") dBrack = Math.max(0, dBrack - 1);
        else if (ch === "{") dBrace++;
        else if (ch === "}") dBrace = Math.max(0, dBrace - 1);
        else if (ch === "," && dParen === 0 && dBrack === 0 && dBrace === 0) {
            argSegments.push(argsText.slice(segStart, i));
            segStart = i + 1;
            argIndex++;
        }
    }
    const currentSegment = argsText.slice(segStart);
    const usedKwargs = new Set();
    for (const seg of argSegments) {
        const m = /^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=/.exec(seg);
        if (m) usedKwargs.add(m[1]);
    }

    const eqMatch = /^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*)$/s.exec(currentSegment);
    const currentKwargName = eqMatch ? eqMatch[1] : null;
    const currentValuePrefix = eqMatch ? eqMatch[2] : "";
    const isKeywordNamePosition = !eqMatch && /^\s*[A-Za-z0-9_]*$/.test(currentSegment);

    const resolved = resolveVapourSynthFunctionFromChain(calleeChain, clipInfo);
    return {
        calleeChain,
        resolved,
        argIndex,
        usedKwargs,
        currentKwargName,
        currentValuePrefix,
        isKeywordNamePosition,
        isValuePosition: Boolean(eqMatch)
    };
}

const PARAMETER_VALUE_PRESETS = {
    matrix_in_s: ['"709"', '"2020ncl"', '"170m"', '"470bg"', '"fcc"', '"rgb"'],
    matrix_s: ['"709"', '"2020ncl"', '"170m"', '"470bg"', '"fcc"', '"rgb"'],
    transfer_in_s: ['"709"', '"st2084"', '"std-b67"', '"linear"', '"srgb"', '"601"'],
    transfer_s: ['"709"', '"st2084"', '"std-b67"', '"linear"', '"srgb"', '"601"'],
    primaries_in_s: ['"709"', '"2020"', '"170m"', '"470bg"', '"display-p3"'],
    primaries_s: ['"709"', '"2020"', '"170m"', '"470bg"', '"display-p3"'],
    range_in_s: ['"limited"', '"full"'],
    range_s: ['"limited"', '"full"'],
    chromaloc_in_s: ['"left"', '"center"', '"top_left"'],
    chromaloc_s: ['"left"', '"center"', '"top_left"'],
    format: ["vs.YUV420P8", "vs.YUV420P10", "vs.YUV420P16", "vs.YUV444P16", "vs.RGB24", "vs.RGBS", "vs.GRAY8", "vs.GRAY16"],
    dmode: ["3", "6", "7", "0", "1"]
};

function createParameterSuggestions(callContext, range) {
    if (!callContext?.resolved) {
        return [];
    }

    const { descriptor, isChained } = callContext.resolved;
    const params = normalizeArray(descriptor.parameters);

    if (callContext.isValuePosition && callContext.currentKwargName) {
        const presets = PARAMETER_VALUE_PRESETS[callContext.currentKwargName];
        if (!presets) {
            return [];
        }
        const trimmedVal = (callContext.currentValuePrefix ?? "").trimStart();
        const insideQuote = trimmedVal.startsWith('"') || trimmedVal.startsWith("'");
        return presets.map((val, idx) => {
            const isQuotedPreset = (val.startsWith('"') && val.endsWith('"')) || (val.startsWith("'") && val.endsWith("'"));
            const textToInsert = insideQuote && isQuotedPreset ? val.slice(1, -1) : val;
            return {
                label: textToInsert,
                kind: monaco.languages.CompletionItemKind.EnumMember,
                detail: `${callContext.currentKwargName} preset`,
                insertText: textToInsert,
                sortText: `00-${String(idx).padStart(2, "0")}-${textToInsert}`,
                range
            };
        });
    }

    if (!callContext.isKeywordNamePosition) {
        return [];
    }

    const suggestions = [];
    for (let i = 0; i < params.length; i++) {
        const param = params[i];
        if (i === 0 && isChained && (param.name === "clip" || param.name === "clips" || param.name === "input")) {
            continue;
        }
        if (param.label.startsWith("*") || callContext.usedKwargs.has(param.name)) {
            continue;
        }
        suggestions.push({
            label: `${param.name}=`,
            kind: monaco.languages.CompletionItemKind.Field,
            detail: param.label,
            documentation: descriptor.signatureLabel,
            insertText: `${param.name}=`,
            filterText: param.name,
            sortText: `00-${String(i).padStart(2, "0")}-${param.name}`,
            range
        });
    }

    return suggestions;
}

function createVsConstantSuggestions(prefix, range) {
    const items = [];
    if (matchesPrefix("core", prefix)) {
        items.push({
            label: "core",
            kind: monaco.languages.CompletionItemKind.Property,
            detail: "VapourSynth Core singleton",
            insertText: "core",
            sortText: "0-core",
            range
        });
    }
    if (matchesPrefix("VideoNode", prefix)) {
        items.push({
            label: "VideoNode",
            kind: monaco.languages.CompletionItemKind.Class,
            detail: "VapourSynth VideoNode",
            insertText: "VideoNode",
            sortText: "0-VideoNode",
            range
        });
    }
    for (const c of languageFeatureIndex.vsConstants) {
        if (!matchesPrefix(c.name, prefix)) continue;
        items.push({
            label: c.name,
            kind: c.kind === "enumMember"
                ? monaco.languages.CompletionItemKind.EnumMember
                : monaco.languages.CompletionItemKind.Constant,
            detail: c.detail,
            documentation: c.documentation,
            insertText: c.name,
            filterText: `vs.${c.name}`,
            sortText: `1-${c.name}`,
            range
        });
    }
    return items;
}

function createClipMemberSuggestions(prefix, range) {
    const items = [];
    for (const m of languageFeatureIndex.videoNodeMembers) {
        if (!matchesPrefix(m.name, prefix)) continue;
        items.push({
            label: m.name,
            kind: m.kind === "method"
                ? monaco.languages.CompletionItemKind.Method
                : monaco.languages.CompletionItemKind.Property,
            detail: m.detail,
            documentation: m.documentation,
            insertText: m.name,
            sortText: `0-${m.name}`,
            range
        });
    }

    for (const ns of languageFeatures.namespaces) {
        if (!matchesPrefix(ns.name, prefix)) continue;
        items.push({
            label: ns.name,
            kind: monaco.languages.CompletionItemKind.Module,
            detail: ns.identifier || ns.displayName,
            documentation: ns.displayName || "",
            insertText: ns.name,
            sortText: `1-${ns.name}`,
            range
        });
    }

    return items;
}

function createEncodingSnippets(model, position, range, clipInfo) {
    const lineBefore = model.getLineContent(position.lineNumber).slice(0, position.column - 1);
    if (/[.\(\[,=]\s*[\w]*$/.test(lineBefore) && !/^\s*[\w]*$/.test(lineBefore)) {
        return [];
    }

    const v = clipInfo.activeVar || "clip";
    const hasNs = (ns) => languageFeatureIndex.namespaceMap.has(ns);
    const hasPkg = (pkg) => languageFeatureIndex.installedPackages.has(pkg);
    const vsAlias = clipInfo.importedModules?.get("vapoursynth") || "vs";
    const hasVsImport = clipInfo.importedModules?.has("vapoursynth") === true;
    const awfAlias = clipInfo.importedModules?.get("awsmfunc") || "awf";
    const hafAlias = clipInfo.importedModules?.get("havsfunc") || "haf";
    const rfsAlias = clipInfo.importedSymbols?.get("replace_ranges") || "replace_ranges";
    const bm3dAlias = clipInfo.importedSymbols?.get("BM3D") || "BM3D";
    const basedAaAlias = clipInfo.importedSymbols?.get("based_aa") || "based_aa";
    const fineDehaloAlias = clipInfo.importedSymbols?.get("fine_dehalo") || "fine_dehalo";
    const placeboDebandAlias = clipInfo.importedSymbols?.get("Placebo") || "Placebo";
    const needsBaseClip = clipInfo.vars.size === 0;
    const baseClipStub = `${v} = core.std.BlankClip(width=1920, height=1080, format=${vsAlias}.YUV420P8)\n`;

    const defs = [
        {
            label: "vsinit",
            detail: "VapourSynth 脚本完整骨架",
            ready: hasNs("lsmas") || hasNs("ffms2"),
            body: `import vapoursynth as vs\ncore = vs.core\n\nsrc = core.lsmas.LWLibavSource(r"\${1:source.mkv}")\n\${0}\nsrc.set_output()`
        },
        {
            label: "lsmas",
            detail: "源加载: lsmas.LWLibavSource",
            ready: hasNs("lsmas"),
            req: "lsmas",
            needsCore: true,
            body: `\${1:src} = core.lsmas.LWLibavSource(r"\${2:source.mkv}")`
        },
        {
            label: "ffms2",
            detail: "源加载: ffms2.Source",
            ready: hasNs("ffms2"),
            req: "ffms2",
            needsCore: true,
            body: `\${1:src} = core.ffms2.Source(r"\${2:source.mkv}")`
        },
        {
            label: "bestsource",
            detail: "源加载: bs.VideoSource (BestSource)",
            ready: hasNs("bs"),
            req: "bestsource",
            needsCore: true,
            body: `\${1:src} = core.bs.VideoSource(r"\${2:source.mkv}")`
        },
        {
            label: "bit16",
            detail: "升位至 16-bit (fmtc.bitdepth)",
            ready: hasNs("fmtc"),
            req: "fmtc",
            needsCore: true,
            usesActiveVar: true,
            body: `\${1:src16} = core.fmtc.bitdepth(${v}, bits=16)`
        },
        {
            label: "dither10",
            detail: "降位至 10-bit 误差扩散抖动 (fmtc.bitdepth)",
            ready: hasNs("fmtc"),
            req: "fmtc",
            needsCore: true,
            usesActiveVar: true,
            body: `\${1:out10} = core.fmtc.bitdepth(${v}, bits=10, dmode=\${2:3})`
        },
        {
            label: "dither8",
            detail: "降位至 8-bit 误差扩散抖动 (fmtc.bitdepth)",
            ready: hasNs("fmtc"),
            req: "fmtc",
            needsCore: true,
            usesActiveVar: true,
            body: `\${1:out8} = core.fmtc.bitdepth(${v}, bits=8, dmode=\${2:3})`
        },
        {
            label: "setprops",
            detail: "设置 BT.709 色彩帧属性 (std.SetFrameProps)",
            ready: hasNs("std"),
            usesActiveVar: true,
            body: `${v} = ${v}.std.SetFrameProps(_Matrix=\${1:1}, _Transfer=\${2:1}, _Primaries=\${3:1}, _ColorRange=\${4:1})`
        },
        {
            label: "hdr2sdr",
            detail: "HDR 转 SDR 映射 (placebo.Tonemap)",
            ready: hasNs("placebo"),
            req: "placebo",
            needsCore: true,
            needsVs: true,
            usesActiveVar: true,
            body: `${v} = core.placebo.Tonemap(${v}.resize.Spline36(format=${vsAlias}.YUV444P16), src_csp=\${1:2}, dst_csp=0, target_peak=\${2:203.0}, tone_mapping_function=3)`
        },
        {
            label: "deband",
            detail: "去色带: placebo.Deband",
            ready: hasNs("placebo"),
            req: "placebo",
            needsCore: true,
            usesActiveVar: true,
            body: `${v} = core.placebo.Deband(${v}, iterations=\${1:1}, threshold=\${2:4.0}, radius=\${3:16.0}, grain=\${4:6.0})`
        },
        {
            label: "vsdeband",
            detail: "去色带: vsdeband.Placebo",
            ready: hasPkg("vsdeband"),
            req: "vsdeband",
            fromImport: ["vsdeband", "Placebo"],
            usesActiveVar: true,
            body: `${v} = ${placeboDebandAlias}.deband(${v}, thr=\${1:2.0}, radius=\${2:16}, grain=\${3:4})`
        },
        {
            label: "f3kdb",
            detail: "去色带: neo_f3kdb.Deband",
            ready: hasNs("neo_f3kdb"),
            req: "neo_f3kdb",
            needsCore: true,
            usesActiveVar: true,
            body: `${v} = core.neo_f3kdb.Deband(${v}, range=\${1:15}, y=\${2:48}, cb=\${3:36}, cr=\${4:36}, grainy=\${5:24}, grainc=0)`
        },
        {
            label: "denoise",
            detail: "降噪: dfttest.DFTTest",
            ready: hasNs("dfttest"),
            req: "dfttest",
            needsCore: true,
            usesActiveVar: true,
            body: `${v} = core.dfttest.DFTTest(${v}, sigma=\${1:2.0})`
        },
        {
            label: "bm3d",
            detail: "降噪: vsdenoise.BM3D / bm3dcuda",
            ready: hasPkg("vsdenoise") || hasNs("bm3dcuda") || hasNs("bm3d"),
            req: "vsdenoise / bm3d",
            needsCore: !hasPkg("vsdenoise"),
            fromImport: hasPkg("vsdenoise") ? ["vsdenoise", "BM3D"] : null,
            usesActiveVar: true,
            body: hasPkg("vsdenoise")
                ? `${v} = ${bm3dAlias}.denoise(${v}, sigma=\${1:1.5}, tr=\${2:2})`
                : `${v} = core.bm3dcuda.BM3D(${v}, sigma=[\${1:1.5}, 0, 0], radius=\${2:2})`
        },
        {
            label: "bbmod",
            detail: "边线修复: awsmfunc.bbmod",
            ready: hasPkg("awsmfunc"),
            req: "awsmfunc",
            moduleImport: ["awsmfunc", "awf"],
            usesActiveVar: true,
            body: `${v} = ${awfAlias}.bbmod(${v}, top=\${1:2}, bottom=\${2:2}, left=\${3:2}, right=\${4:2}, thresh=\${5:32}, blur=\${6:20})`
        },
        {
            label: "aa",
            detail: "抗锯齿: vsaa.based_aa",
            ready: hasPkg("vsaa") || hasPkg("vsjetpack"),
            req: "vsaa / vsjetpack",
            fromImport: ["vsaa", "based_aa"],
            usesActiveVar: true,
            body: `${v} = ${basedAaAlias}(${v}, rfactor=\${1:2.0})`
        },
        {
            label: "dehalo",
            detail: "去光晕: vsdehalo / havsfunc.FineDehalo",
            ready: hasPkg("vsdehalo") || hasPkg("havsfunc"),
            req: "vsdehalo / havsfunc",
            fromImport: hasPkg("vsdehalo") ? ["vsdehalo", "fine_dehalo"] : null,
            moduleImport: !hasPkg("vsdehalo") ? ["havsfunc", "haf"] : null,
            usesActiveVar: true,
            body: hasPkg("vsdehalo")
                ? `${v} = ${fineDehaloAlias}(${v}, rx=\${1:2.0}, ry=\${2:2.0})`
                : `${v} = ${hafAlias}.FineDehalo(${v}, rx=\${1:2.0}, ry=\${2:2.0})`
        },
        {
            label: "descale",
            detail: "逆缩放: descale.Debicubic",
            ready: hasNs("descale"),
            req: "descale",
            needsCore: true,
            usesActiveVar: true,
            body: `${v} = core.descale.Debicubic(${v}, width=\${1:1280}, height=\${2:720}, b=\${3:0.0}, c=\${4:0.5})`
        },
        {
            label: "qtgmc",
            detail: "反交错: havsfunc.QTGMC",
            ready: hasPkg("havsfunc"),
            req: "havsfunc",
            moduleImport: ["havsfunc", "haf"],
            usesActiveVar: true,
            body: `${v} = ${hafAlias}.QTGMC(${v}, Preset="\${1:Slow}", TFF=\${2:True}, FPSDivisor=\${3:2})`
        },
        {
            label: "ivtc",
            detail: "反胶片过带: vivtc.VFM + VDecimate",
            ready: hasNs("vivtc"),
            req: "vivtc",
            needsCore: true,
            usesActiveVar: true,
            body: `${v} = core.vivtc.VFM(${v}, order=\${1:1})\n${v} = core.vivtc.VDecimate(${v})`
        },
        {
            label: "edgemask",
            detail: "边缘遮罩合并: std.MaskedMerge",
            ready: hasNs("std"),
            needsCore: true,
            usesActiveVar: true,
            body: clipInfo.vars.has("flt")
                ? `mask = ${v}.std.Prewitt().std.Maximum()\n${v} = core.std.MaskedMerge(\${1:flt}, ${v}, mask)`
                : `flt = ${v}  # 替换为滤镜处理\nmask = ${v}.std.Prewitt().std.Maximum()\n${v} = core.std.MaskedMerge(\${1:flt}, ${v}, mask)`
        },
        {
            label: "rfs",
            detail: "帧区间局部替换: vstools.replace_ranges",
            ready: hasPkg("vstools") || hasPkg("vsjetpack"),
            req: "vstools / vsjetpack",
            fromImport: ["vstools", "replace_ranges"],
            usesActiveVar: true,
            body: clipInfo.vars.has("flt")
                ? `${v} = ${rfsAlias}(${v}, \${1:flt}, [(\${2:0}, \${3:100})])`
                : `flt = ${v}  # 替换为滤镜处理\n${v} = ${rfsAlias}(${v}, \${1:flt}, [(\${2:0}, \${3:100})])`
        },
        {
            label: "frameinfo",
            detail: "帧信息叠加: awsmfunc.FrameInfo",
            ready: hasPkg("awsmfunc"),
            req: "awsmfunc",
            moduleImport: ["awsmfunc", "awf"],
            usesActiveVar: true,
            body: `${v} = ${awfAlias}.FrameInfo(${v}, title="\${1:Clip}")`
        },
        {
            label: "vscmp",
            detail: "双路输出对比骨架 (Output 0 vs Output 1)",
            ready: true,
            usesActiveVar: true,
            body: clipInfo.vars.has("flt")
                ? `\${1:${v}}.set_output(0)\n\${2:flt}.set_output(1)`
                : `flt = ${v}  # 替换为对比处理画面\n\${1:${v}}.set_output(0)\n\${2:flt}.set_output(1)`
        }
    ];

    const prefix = getWordRange(position);
    const typed = model.getValueInRange(prefix).toLowerCase();
    const importInsertLine = clipInfo.lastImportLine > 0 ? clipInfo.lastImportLine + 1 : 1;

    return defs
        .filter((d) => typed.length === 0 || d.label.toLowerCase().startsWith(typed))
        .map((d) => {
            const missingHeaderLines = [];
            const needsRuntime = Boolean(d.needsCore || d.needsVs || (d.usesActiveVar && needsBaseClip));
            if (needsRuntime && !hasVsImport && (d.needsVs || !clipInfo.hasCore || (d.usesActiveVar && needsBaseClip))) {
                missingHeaderLines.push("import vapoursynth as vs");
            }
            if (d.moduleImport && !clipInfo.importedModules?.has(d.moduleImport[0])) {
                missingHeaderLines.push(`import ${d.moduleImport[0]} as ${d.moduleImport[1]}`);
            }
            if (d.fromImport && !clipInfo.importedSymbols?.has(d.fromImport[1])) {
                missingHeaderLines.push(`from ${d.fromImport[0]} import ${d.fromImport[1]}`);
            }
            if ((d.needsCore || (d.usesActiveVar && needsBaseClip)) && !clipInfo.hasCore) {
                missingHeaderLines.push(`core = ${vsAlias}.core`);
            }

            let insertText = d.usesActiveVar && needsBaseClip ? (baseClipStub + d.body) : d.body;
            let additionalTextEdits = undefined;

            if (missingHeaderLines.length > 0) {
                const headerBlock = missingHeaderLines.join("\n") + "\n";
                if (importInsertLine < position.lineNumber) {
                    additionalTextEdits = [{
                        range: new monaco.Range(importInsertLine, 1, importInsertLine, 1),
                        text: headerBlock
                    }];
                } else {
                    insertText = headerBlock + insertText;
                }
            }

            const item = {
                label: d.label,
                kind: monaco.languages.CompletionItemKind.Snippet,
                detail: d.ready ? `${d.detail} ✓` : `${d.detail} [缺少 ${d.req}]`,
                documentation: insertText.replace(/\$\{[0-9]+:([^}]*)\}/g, "$1").replace(/\$\{[0-9]+\}/g, ""),
                insertText,
                insertTextRules: monaco.languages.CompletionItemInsertTextRule.InsertAsSnippet,
                sortText: `${d.ready ? "2" : "8"}-${d.label}`,
                range
            };
            if (additionalTextEdits) {
                item.additionalTextEdits = additionalTextEdits;
            }
            return item;
        });
}

function buildMemberDocumentation(member) {
    const lines = [];
    if (member.documentation) {
        lines.push(member.documentation);
    }
    if (member.detail) {
        lines.push(`\`${member.detail}\``);
    }

    return lines.join("\n\n");
}

function createRootMemberSuggestions(prefix, range) {
    return languageFeatureIndex.rootMembers
        .filter((member) => matchesPrefix(member.name, prefix))
        .map((member) => ({
            label: member.name,
            kind: member.kind === "namespace"
                ? monaco.languages.CompletionItemKind.Module
                : member.kind === "method"
                    ? monaco.languages.CompletionItemKind.Function
                    : monaco.languages.CompletionItemKind.Property,
            detail: member.detail,
            documentation: buildMemberDocumentation(member),
            insertText: member.name,
            filterText: `core.${member.name}`,
            sortText: `${member.kind === "namespace" ? "0" : "1"}-${member.name}`,
            range
        }));
}

function createNamespaceFunctionSuggestions(namespaceName, prefix, range) {
    const namespaceItem = languageFeatureIndex.namespaceMap.get(namespaceName);
    if (!namespaceItem) {
        return [];
    }

    return namespaceItem.functions
        .filter((functionItem) => matchesPrefix(functionItem.name, prefix))
        .map((functionItem) => ({
            label: functionItem.name,
            kind: monaco.languages.CompletionItemKind.Function,
            detail: functionItem.signatureLabel,
            documentation: functionItem.documentation || namespaceItem.displayName,
            insertText: functionItem.name,
            filterText: functionItem.qualifiedName,
            sortText: `0-${functionItem.name}`,
            range
        }));
}

function provideVapourSynthCompletions(model, position) {
    if (!languageFeatures.isRuntimeReady) {
        return { suggestions: [] };
    }

    const clipInfo = collectModelClipVariables(model);
    const range = getWordRange(position);
    const token = getChainTokenBeforeCursor(model, position);
    const chain = parseVapourSynthChain(token, clipInfo);

    if (chain) {
        const segments = chain.normalizedSegments;
        if (chain.mode === "vs") {
            if (segments.length <= 2) {
                return { suggestions: createVsConstantSuggestions(segments[1] ?? "", range) };
            }
            return { suggestions: [] };
        }

        if (chain.mode === "core") {
            if (segments.length <= 2) {
                return { suggestions: createRootMemberSuggestions(segments[1] ?? "", range) };
            }
            return { suggestions: createNamespaceFunctionSuggestions(segments[1], segments[2] ?? "", range) };
        }

        if (chain.mode === "clip") {
            if (segments.length <= 2) {
                return { suggestions: createClipMemberSuggestions(segments[1] ?? "", range) };
            }
            return { suggestions: createNamespaceFunctionSuggestions(segments[1], segments[2] ?? "", range) };
        }
    }

    const callContext = findEnclosingCallContext(model, position, clipInfo);
    const paramSuggestions = createParameterSuggestions(callContext, range);
    if (paramSuggestions.length > 0) {
        return { suggestions: paramSuggestions };
    }

    return {
        suggestions: createEncodingSnippets(model, position, range, clipInfo)
    };
}

function buildLocalSignatureHelp(model, position) {
    if (!languageFeatures.isRuntimeReady) {
        return null;
    }

    const clipInfo = collectModelClipVariables(model);
    const callContext = findEnclosingCallContext(model, position, clipInfo);
    if (!callContext?.resolved) {
        return null;
    }

    const { descriptor, isChained } = callContext.resolved;
    const allParams = normalizeArray(descriptor.parameters);
    const visibleParams = isChained && allParams.length > 0
        && (allParams[0].name === "clip" || allParams[0].name === "clips" || allParams[0].name === "input")
        ? allParams.slice(1)
        : allParams;

    let activeParam = callContext.argIndex;
    if (callContext.currentKwargName) {
        const kwIdx = visibleParams.findIndex((p) => p.name === callContext.currentKwargName);
        if (kwIdx >= 0) {
            activeParam = kwIdx;
        }
    }
    if (visibleParams.length > 0) {
        activeParam = Math.max(0, Math.min(activeParam, visibleParams.length - 1));
    } else {
        activeParam = 0;
    }

    const label = `${descriptor.namespaceName}.${descriptor.name}(${visibleParams.map((p) => p.label).join(", ")})`;
    return {
        value: {
            signatures: [{
                label,
                documentation: descriptor.documentation || descriptor.namespaceDisplayName || "",
                parameters: visibleParams.map((p) => ({
                    label: p.label,
                    documentation: p.documentation || ""
                }))
            }],
            activeSignature: 0,
            activeParameter: activeParam
        },
        dispose() {}
    };
}

async function providePythonSignatureHelp(model, position, cancellationToken, context) {
    const local = buildLocalSignatureHelp(model, position);
    if (local) {
        return local;
    }

    try {
        const lsp = await requestLanguageFeature(
            "signatureHelp",
            buildLanguageRequestPayload(model, position, {
                triggerCharacter: normalizeText(context?.triggerCharacter)
            }),
            cancellationToken);
        if (lsp && Array.isArray(lsp.signatures) && lsp.signatures.length > 0) {
            return {
                value: {
                    signatures: lsp.signatures.map((sig) => ({
                        label: sig.label ?? "",
                        documentation: sig.documentation ?? "",
                        parameters: normalizeArray(sig.parameters).map((p) => ({
                            label: p.label ?? "",
                            documentation: p.documentation ?? ""
                        }))
                    })),
                    activeSignature: Number(lsp.activeSignature ?? 0),
                    activeParameter: Number(lsp.activeParameter ?? 0)
                },
                dispose() {}
            };
        }
    } catch {
    }

    return null;
}

function buildLocalHover(model, position) {
    if (!languageFeatures.isRuntimeReady) {
        return null;
    }

    const clipInfo = collectModelClipVariables(model);
    const { token, startColumn, endColumn } = getChainTokenAtPosition(model, position);
    if (!token) {
        return null;
    }

    const parsed = parseVapourSynthChain(token, clipInfo);
    if (!parsed) {
        return null;
    }

    const range = new monaco.Range(position.lineNumber, startColumn, position.lineNumber, endColumn);
    const segs = parsed.normalizedSegments;

    if (parsed.mode === "vs" && segs.length === 2) {
        const c = languageFeatureIndex.vsConstantMap.get(segs[1]);
        if (c) {
            return {
                range,
                contents: [
                    { value: `\`\`\`python\nvs.${c.name}  # ${c.detail}\n\`\`\`` },
                    { value: c.documentation || "" }
                ]
            };
        }
    }

    if ((parsed.mode === "core" || parsed.mode === "clip") && segs.length === 2) {
        if (parsed.mode === "clip") {
            const member = languageFeatureIndex.videoNodeMemberMap.get(segs[1]);
            if (member) {
                return {
                    range,
                    contents: [
                        { value: `\`\`\`python\nVideoNode.${member.detail}\n\`\`\`` },
                        { value: member.documentation || "" }
                    ]
                };
            }
        }
        const ns = languageFeatureIndex.namespaceMap.get(segs[1]);
        if (ns) {
            return {
                range,
                contents: [
                    { value: `**VapourSynth Plugin** \`${ns.name}\`` },
                    { value: `${ns.displayName || ns.name} (${ns.functions.length} functions)` }
                ]
            };
        }
    }

    if ((parsed.mode === "core" || parsed.mode === "clip") && segs.length >= 3) {
        const desc = languageFeatureIndex.functionMap.get(`core.${segs[1]}.${segs[2]}`);
        if (desc) {
            const ret = desc.returnType ? ` -> ${desc.returnType}` : "";
            return {
                range,
                contents: [
                    { value: `\`\`\`python\n${desc.qualifiedName}(${normalizeArray(desc.parameters).map((p) => p.label).join(", ")})${ret}\n\`\`\`` },
                    { value: desc.documentation || desc.namespaceDisplayName || "" }
                ]
            };
        }
    }

    return null;
}

async function providePythonHover(model, position, cancellationToken) {
    const local = buildLocalHover(model, position);
    if (local) {
        return local;
    }

    try {
        const lsp = await requestLanguageFeature(
            "hover",
            buildLanguageRequestPayload(model, position),
            cancellationToken);
        if (lsp && typeof lsp.contents === "string" && lsp.contents.trim().length > 0) {
            return {
                contents: [{ value: lsp.contents }]
            };
        }
    } catch {
    }

    return null;
}

async function providePythonCompletions(model, position, context, cancellationToken) {
    const staticSuggestions = provideVapourSynthCompletions(model, position).suggestions;
    let lspSuggestions = [];

    try {
        const lspItems = await requestLanguageFeature(
            "completion",
            buildLanguageRequestPayload(model, position, {
                triggerCharacter: normalizeText(context?.triggerCharacter)
            }),
            cancellationToken);

        lspSuggestions = mapLspCompletionItems(lspItems, getWordRange(position));
    } catch {
    }

    return {
        suggestions: mergeCompletionSuggestions(staticSuggestions, lspSuggestions)
    };
}

function collectSafePythonIdentifierEdits(model, oldName, newName) {
    const edits = [];
    if (!oldName || !newName || oldName === newName || !/^[A-Za-z_][A-Za-z0-9_]*$/.test(newName)) {
        return edits;
    }

    const lineCount = model.getLineCount();
    let tripleQuote = null;

    for (let lineNum = 1; lineNum <= lineCount; lineNum++) {
        const line = model.getLineContent(lineNum);
        let i = 0;
        let activeQuote = null;

        while (i < line.length) {
            if (tripleQuote) {
                if (line.startsWith(tripleQuote, i)) {
                    tripleQuote = null;
                    i += 3;
                } else {
                    i++;
                }
                continue;
            }

            const ch = line[i];
            if (activeQuote) {
                if (ch === "\\") {
                    i += 2;
                    continue;
                }
                if (ch === activeQuote) {
                    activeQuote = null;
                }
                i++;
                continue;
            }

            if (ch === "#") {
                break;
            }

            if (line.startsWith('"""', i) || line.startsWith("'''", i)) {
                tripleQuote = line.slice(i, i + 3);
                i += 3;
                continue;
            }

            if (ch === '"' || ch === "'") {
                activeQuote = ch;
                i++;
                continue;
            }

            if (/[A-Za-z_]/.test(ch)) {
                let start = i;
                while (i < line.length && /[A-Za-z0-9_]/.test(line[i])) {
                    i++;
                }
                const ident = line.slice(start, i);
                if (ident === oldName) {
                    const prevNonWs = line.slice(0, start).trimEnd();
                    const isMemberAccess = prevNonWs.endsWith(".");
                    if (!isMemberAccess) {
                        edits.push({
                            resource: model.uri,
                            versionId: model.getVersionId(),
                            textEdit: {
                                range: new monaco.Range(lineNum, start + 1, lineNum, i + 1),
                                text: newName
                            }
                        });
                    }
                }
                continue;
            }

            i++;
        }
    }

    return edits;
}

function applySmartDocumentEdit(payload) {
    if (!editorInstance || !editorModel || typeof monaco === "undefined") {
        return false;
    }

    const nextText = typeof payload?.text === "string" ? payload.text : null;
    if (nextText === null) {
        return false;
    }

    const revealLine = Math.max(1, Number(payload?.revealLine ?? 1));
    editorInstance.pushUndoStop();
    editorInstance.executeEdits("flowencode-smart-edit", [{
        range: editorModel.getFullModelRange(),
        text: nextText,
        forceMoveMarkers: true
    }]);
    editorInstance.pushUndoStop();

    const clampedLine = Math.min(revealLine, editorModel.getLineCount());
    editorInstance.setPosition({ lineNumber: clampedLine, column: 1 });
    editorInstance.revealLineInCenterIfOutsideViewport(clampedLine);
    queueBufferBroadcast();
    return true;
}

function registerLanguageProviders() {
    monaco.languages.registerCompletionItemProvider("python", {
        triggerCharacters: [".", "(", ",", "="],
        provideCompletionItems(model, position, context, cancellationToken) {
            return providePythonCompletions(model, position, context, cancellationToken);
        }
    });

    monaco.languages.registerSignatureHelpProvider("python", {
        signatureHelpTriggerCharacters: ["(", ","],
        signatureHelpRetriggerCharacters: [","],
        provideSignatureHelp(model, position, cancellationToken, context) {
            return providePythonSignatureHelp(model, position, cancellationToken, context);
        }
    });

    monaco.languages.registerHoverProvider("python", {
        provideHover(model, position, cancellationToken) {
            return providePythonHover(model, position, cancellationToken);
        }
    });

    monaco.languages.registerCodeActionProvider("python", {
        provideCodeActions(model, range) {
            const actions = [];
            for (const diag of latestDiagnostics) {
                const hasReplace = Boolean(diag.suggestedReplacement);
                const hasInsert = Boolean(diag.quickFixInsertText);
                if (!hasReplace && !hasInsert) {
                    continue;
                }
                const diagRange = new monaco.Range(
                    Math.max(1, diag.startLine ?? 1),
                    Math.max(1, diag.startColumn ?? 1),
                    Math.max(1, diag.endLine ?? diag.startLine ?? 1),
                    Math.max(1, diag.endColumn ?? diag.startColumn ?? 1));
                if (!monaco.Range.areIntersectingOrTouching(range, diagRange)) {
                    continue;
                }

                const editRange = hasInsert
                    ? (() => {
                        const targetLine = Math.max(1, Math.min(Number(diag.quickFixInsertLine ?? 1), model.getLineCount() + 1));
                        if (targetLine > model.getLineCount()) {
                            const lastLine = model.getLineCount();
                            const lastCol = model.getLineMaxColumn(lastLine);
                            return new monaco.Range(lastLine, lastCol, lastLine, lastCol);
                        }
                        return new monaco.Range(targetLine, 1, targetLine, 1);
                    })()
                    : diagRange;
                const editText = hasInsert
                    ? (Number(diag.quickFixInsertLine ?? 1) > model.getLineCount() ? "\n" + diag.quickFixInsertText : diag.quickFixInsertText)
                    : diag.suggestedReplacement;

                actions.push({
                    title: diag.quickFixTitle || (hasInsert ? "Insert import" : ("Replace with '" + diag.suggestedReplacement + "'")),
                    kind: "quickfix",
                    isPreferred: true,
                    edit: {
                        edits: [{
                            resource: model.uri,
                            versionId: model.getVersionId(),
                            textEdit: {
                                range: editRange,
                                text: editText
                            }
                        }]
                    }
                });
            }
            return { actions, dispose() {} };
        }
    });

    monaco.languages.registerRenameProvider("python", {
        resolveRenameLocation(model, position) {
            const word = model.getWordAtPosition(position);
            if (!word || !/^[A-Za-z_][A-Za-z0-9_]*$/.test(word.word)) {
                return { rejectReason: "Cannot rename this token." };
            }
            return {
                range: new monaco.Range(position.lineNumber, word.startColumn, position.lineNumber, word.endColumn),
                text: word.word
            };
        },
        provideRenameEdits(model, position, newName) {
            const word = model.getWordAtPosition(position);
            if (!word) {
                return { edits: [] };
            }
            return {
                edits: collectSafePythonIdentifierEdits(model, word.word, newName)
            };
        }
    });
}

function createEditor() {
    const editorLineHeight = 22;
    const editorBottomPaddingLines = 10;

    editorModel = monaco.editor.createModel(
        "",
        "python",
        monaco.Uri.parse("inmemory:///vapoursynth/current.vpy"));

    editorInstance = monaco.editor.create(editorContainer, {
        model: editorModel,
        automaticLayout: true,
        tabSize: 4,
        indentSize: 4,
        insertSpaces: true,
        detectIndentation: false,
        fontFamily: "Cascadia Code, Consolas, SFMono-Regular, monospace",
        fontSize: 14,
        lineHeight: editorLineHeight,
        cursorBlinking: "solid",
        smoothScrolling: true,
        scrollBeyondLastLine: false,
        padding: {
            top: 8,
            bottom: editorLineHeight * editorBottomPaddingLines
        },
        minimap: { enabled: false },
        folding: true,
        glyphMargin: true,
        lineNumbersMinChars: 4,
        renderWhitespace: "selection",
        renderLineHighlight: "all",
        roundedSelection: false,
        colorDecorators: false,
        wordWrap: "off",
        bracketPairColorization: { enabled: true },
        guides: {
            indentation: true,
            highlightActiveIndentation: true,
            bracketPairs: true
        },
        matchBrackets: "always",
        autoClosingBrackets: "always",
        autoClosingQuotes: "always",
        autoSurround: "languageDefined",
        quickSuggestions: {
            other: true,
            comments: false,
            strings: false
        },
        suggestOnTriggerCharacters: true,
        acceptSuggestionOnEnter: "smart",
        parameterHints: { enabled: true },
        renderValidationDecorations: "on",
        contextmenu: true,
        overviewRulerLanes: 0,
        hideCursorInOverviewRuler: true,
        overviewRulerBorder: false,
        stickyScroll: {
            enabled: false
        }
    });

    registerLanguageProviders();
    registerHostCommands();

    editorModel.onDidChangeContent(() => {
        if (suppressModelEvents) {
            return;
        }

        queueBufferBroadcast();
    });

    editorInstance.onDidChangeCursorPosition(() => {
        queueCursorBroadcast();
    });

    editorInstance.onDidFocusEditorText(() => {
        queueCursorBroadcast();
    });

    editorInstance.onDidBlurEditorText(() => {
        queueCursorBroadcast();
    });

    applyTheme();
    loadDocument({
        text: pendingDocumentText,
        filePath: pendingDocumentFilePath
    }, {
        broadcastState: false
    });
    postMessage({ type: "ready" });
}

window.MonacoEnvironment = {
    baseUrl: "./vendor/monaco/vs"
};

window.vsWorkspaceHost = {
    applyHostTheme,
    applyDiagnostics,
    applySmartDocumentEdit,
    captureStateJson,
    executeCommand,
    insertSnippet,
    insertText,
    loadDocument,
    loadLanguageFeatures,
    resolveLanguageRequest
};

window.addEventListener("error", (event) => {
    postMessage({
        type: "bridgeError",
        message: event.message ?? "Unknown editor error."
    });
});

window.addEventListener("unhandledrejection", (event) => {
    if (event.reason?.name === "Canceled") {
        return;
    }

    const message = event.reason?.message ?? String(event.reason ?? "Unhandled editor promise rejection.");
    postMessage({
        type: "bridgeError",
        message
    });
});

colorScheme.addEventListener("change", () => {
    if (typeof monaco === "undefined") {
        updateDocumentTheme();
        return;
    }

    applyTheme();
});

updateDocumentTheme();

require.config({
    paths: {
        vs: "./vendor/monaco/vs"
    }
});

require(["vs/editor/editor.main"], () => {
    defineThemes();
    createEditor();
});
