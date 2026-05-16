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
        namespaces: []
    };
}

function postMessage(payload) {
    if (window.chrome?.webview) {
        window.chrome.webview.postMessage(payload);
    }
}

function normalizeText(text) {
    return typeof text === "string" ? text : "";
}

function normalizeArray(value) {
    return Array.isArray(value) ? value : [];
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

function mapLspRange(range, fallbackRange) {
    if (!range) {
        return fallbackRange ?? null;
    }

    return {
        startLineNumber: Math.max(1, range.startLine ?? fallbackRange?.startLineNumber ?? 1),
        endLineNumber: Math.max(1, range.endLine ?? fallbackRange?.endLineNumber ?? 1),
        startColumn: Math.max(1, range.startColumn ?? fallbackRange?.startColumn ?? 1),
        endColumn: Math.max(1, range.endColumn ?? fallbackRange?.endColumn ?? 1)
    };
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

function mapLspHover(payload, fallbackRange) {
    const markdown = normalizeText(payload?.markdown).trim();
    if (!markdown) {
        return null;
    }

    return {
        range: mapLspRange(payload?.range, fallbackRange) ?? fallbackRange,
        contents: [
            {
                value: markdown
            }
        ]
    };
}

function buildHoverContentKey(content) {
    if (typeof content?.value === "string") {
        return `markdown:${content.value}`;
    }

    return JSON.stringify(content);
}

function mergeHoverResults(primaryHover, secondaryHover) {
    if (!primaryHover) {
        return secondaryHover;
    }

    if (!secondaryHover) {
        return primaryHover;
    }

    const contents = [];
    const seen = new Set();

    for (const content of [...normalizeArray(primaryHover.contents), ...normalizeArray(secondaryHover.contents)]) {
        const key = buildHoverContentKey(content);
        if (seen.has(key)) {
            continue;
        }

        seen.add(key);
        contents.push(content);
    }

    return {
        range: primaryHover.range ?? secondaryHover.range,
        contents
    };
}

function mapLspSignatureHelp(payload) {
    const signatures = normalizeArray(payload?.signatures)
        .filter((signature) => signature && typeof signature.label === "string")
        .map((signature) => ({
            label: signature.label,
            documentation: normalizeText(signature.documentation) || undefined,
            parameters: normalizeArray(signature.parameters)
                .filter((parameter) => parameter && typeof parameter.label === "string")
                .map((parameter) => ({
                    label: parameter.label,
                    documentation: normalizeText(parameter.documentation) || undefined
                }))
        }));

    if (signatures.length === 0) {
        return null;
    }

    return {
        value: {
            activeSignature: Math.max(0, payload?.activeSignature ?? 0),
            activeParameter: Math.max(0, payload?.activeParameter ?? 0),
            signatures
        },
        dispose() {
        }
    };
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

    return {
        isRuntimeReady: payload?.isRuntimeReady === true,
        runtimeSummary: payload?.runtimeSummary ?? "",
        coreMembers,
        namespaces
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

    return {
        rootMembers: features.coreMembers,
        namespaceMap,
        functionMap
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

function applyDiagnostics(payload) {
    if (!editorModel || typeof monaco === "undefined") {
        return;
    }

    const diagnostics = normalizeArray(payload?.diagnostics);
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
            charCount: 0
        };
    }

    const position = editorInstance.getPosition() ?? { lineNumber: 1, column: 1 };
    const text = editorModel.getValue(monaco.editor.EndOfLinePreference.LF);

    return {
        text,
        line: position.lineNumber,
        column: position.column,
        lineCount: editorModel.getLineCount(),
        charCount: text.length
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
    pendingDocumentText = normalizeText(payload?.text);
    pendingDocumentFilePath = normalizeText(payload?.filePath);
    currentDocumentPath = pendingDocumentFilePath;
    cancelPendingLanguageRequests("Document changed.");

    if (!editorModel || !editorInstance || typeof monaco === "undefined") {
        return;
    }

    suppressModelEvents = true;
    editorModel.setValue(pendingDocumentText);
    editorModel.setEOL(monaco.editor.EndOfLineSequence.LF);
    editorInstance.setScrollPosition({ scrollTop: 0, scrollLeft: 0 });
    editorInstance.setPosition({ lineNumber: 1, column: 1 });
    editorInstance.revealPositionInCenter({ lineNumber: 1, column: 1 });
    suppressModelEvents = false;

    clearDiagnostics();
    if (shouldBroadcastState) {
        queueBufferBroadcast();
        queueCursorBroadcast();
    }
    focusEditor();
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
        case "signatureHelp":
            editorInstance.focus();
            await runAction("editor.action.triggerParameterHints");
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
            "editorHoverWidget.background": "#fffaf3",
            "editorHoverWidget.border": "#d7c5b2",
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
            "editorHoverWidget.background": "#202a31",
            "editorHoverWidget.border": "#30414d",
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
    editorInstance.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyMod.Shift | monaco.KeyCode.Space, () => {
        void executeCommand("signatureHelp");
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

function parseCoreChain(token) {
    if (!token) {
        return null;
    }

    const segments = token.split(".");
    if (segments[0] === "vs" && segments[1] === "core") {
        return {
            raw: token,
            normalizedSegments: ["core", ...segments.slice(2)]
        };
    }

    if (segments[0] === "core") {
        return {
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

function getQualifiedTokenAtPosition(model, position) {
    const lineText = model.getLineContent(position.lineNumber);
    let left = position.column - 2;
    let right = position.column - 1;

    while (left >= 0 && /[\w.]/.test(lineText[left])) {
        left -= 1;
    }

    while (right < lineText.length && /[\w.]/.test(lineText[right])) {
        right += 1;
    }

    if (right <= left + 1) {
        return null;
    }

    return {
        token: lineText.slice(left + 1, right),
        range: {
            startLineNumber: position.lineNumber,
            endLineNumber: position.lineNumber,
            startColumn: left + 2,
            endColumn: right + 1
        }
    };
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

function buildFunctionDocumentation(functionItem) {
    const lines = [`**${functionItem.qualifiedName}**`];

    if (functionItem.documentation) {
        lines.push(functionItem.documentation);
    }

    if (functionItem.returnType) {
        lines.push(`Returns: \`${functionItem.returnType}\``);
    }

    return {
        value: lines.join("\n\n")
    };
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
            documentation: buildFunctionDocumentation(functionItem),
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

    const chain = parseCoreChain(getChainTokenBeforeCursor(model, position));
    if (!chain || chain.normalizedSegments[0] !== "core") {
        return { suggestions: [] };
    }

    const range = getWordRange(position);
    const segments = chain.normalizedSegments;

    if (segments.length === 1) {
        return {
            suggestions: createRootMemberSuggestions("", range)
        };
    }

    if (segments.length === 2) {
        return {
            suggestions: createRootMemberSuggestions(segments[1] ?? "", range)
        };
    }

    return {
        suggestions: createNamespaceFunctionSuggestions(
            segments[1],
            segments[2] ?? "",
            range)
    };
}

function getCallInfo(model, position) {
    const textBeforeCursor = model.getValueInRange({
        startLineNumber: 1,
        startColumn: 1,
        endLineNumber: position.lineNumber,
        endColumn: position.column
    });

    let depth = 0;
    let activeParameter = 0;
    let openParenIndex = -1;

    for (let index = textBeforeCursor.length - 1; index >= 0; index -= 1) {
        const current = textBeforeCursor[index];
        if (current === ")" || current === "]" || current === "}") {
            depth += 1;
            continue;
        }

        if (current === "(" || current === "[" || current === "{") {
            if (depth === 0) {
                if (current !== "(") {
                    return null;
                }

                openParenIndex = index;
                break;
            }

            depth -= 1;
            continue;
        }

        if (current === "," && depth === 0) {
            activeParameter += 1;
        }
    }

    if (openParenIndex < 0) {
        return null;
    }

    let end = openParenIndex;
    let start = openParenIndex - 1;
    while (start >= 0 && /\s/.test(textBeforeCursor[start])) {
        start -= 1;
    }
    end = start + 1;

    while (start >= 0 && /[\w.]/.test(textBeforeCursor[start])) {
        start -= 1;
    }

    const token = textBeforeCursor.slice(start + 1, end);
    const chain = parseCoreChain(token);
    if (!chain || chain.normalizedSegments.length < 3) {
        return null;
    }

    return {
        qualifiedName: normalizeQualifiedName(chain.normalizedSegments.slice(0, 3).join(".")),
        activeParameter
    };
}

function provideVapourSynthSignatureHelp(model, position) {
    if (!languageFeatures.isRuntimeReady) {
        return null;
    }

    const callInfo = getCallInfo(model, position);
    if (!callInfo) {
        return null;
    }

    const functionItem = languageFeatureIndex.functionMap.get(callInfo.qualifiedName);
    if (!functionItem) {
        return null;
    }

    return {
        value: {
            activeSignature: 0,
            activeParameter: Math.max(
                0,
                Math.min(callInfo.activeParameter, Math.max(functionItem.parameters.length - 1, 0))),
            signatures: [{
                label: functionItem.signatureLabel,
                documentation: buildFunctionDocumentation(functionItem),
                parameters: functionItem.parameters.map((parameter) => ({
                    label: parameter.label,
                    documentation: parameter.documentation || undefined
                }))
            }]
        },
        dispose() {
        }
    };
}

function provideVapourSynthHover(model, position) {
    if (!languageFeatures.isRuntimeReady) {
        return null;
    }

    const tokenResult = getQualifiedTokenAtPosition(model, position);
    if (!tokenResult) {
        return null;
    }

    const chain = parseCoreChain(tokenResult.token);
    if (!chain || chain.normalizedSegments[0] !== "core") {
        return null;
    }

    if (chain.normalizedSegments.length >= 3) {
        const functionItem = languageFeatureIndex.functionMap.get(
            normalizeQualifiedName(chain.normalizedSegments.slice(0, 3).join(".")));
        if (functionItem) {
            return {
                range: tokenResult.range,
                contents: [
                    {
                        value: [
                            `**${functionItem.signatureLabel}**`,
                            functionItem.documentation,
                            functionItem.returnType ? `Returns: \`${functionItem.returnType}\`` : ""
                        ].filter(Boolean).join("\n\n")
                    }
                ]
            };
        }
    }

    if (chain.normalizedSegments.length === 2) {
        const memberName = chain.normalizedSegments[1];
        const namespaceItem = languageFeatureIndex.namespaceMap.get(memberName);
        if (namespaceItem) {
            return {
                range: tokenResult.range,
                contents: [
                    {
                        value: [
                            `**core.${namespaceItem.name}**`,
                            namespaceItem.displayName,
                            namespaceItem.identifier ? `\`${namespaceItem.identifier}\`` : ""
                        ].filter(Boolean).join("\n\n")
                    }
                ]
            };
        }
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

async function providePythonSignatureHelp(model, position, cancellationToken) {
    const staticSignatureHelp = provideVapourSynthSignatureHelp(model, position);
    if (staticSignatureHelp) {
        return staticSignatureHelp;
    }

    try {
        const signatureHelp = await requestLanguageFeature(
            "signatureHelp",
            buildLanguageRequestPayload(model, position),
            cancellationToken);

        return mapLspSignatureHelp(signatureHelp);
    } catch {
        return null;
    }
}

async function providePythonHover(model, position, cancellationToken) {
    const staticHover = provideVapourSynthHover(model, position);
    const fallbackRange = staticHover?.range ?? getWordRange(position);

    try {
        const hover = await requestLanguageFeature(
            "hover",
            buildLanguageRequestPayload(model, position),
            cancellationToken);

        return mergeHoverResults(staticHover, mapLspHover(hover, fallbackRange));
    } catch {
        return staticHover;
    }
}

function registerLanguageProviders() {
    monaco.languages.registerCompletionItemProvider("python", {
        triggerCharacters: ["."],
        provideCompletionItems(model, position, context, cancellationToken) {
            return providePythonCompletions(model, position, context, cancellationToken);
        }
    });

    monaco.languages.registerSignatureHelpProvider("python", {
        signatureHelpTriggerCharacters: ["(", ","],
        signatureHelpRetriggerCharacters: [","],
        provideSignatureHelp(model, position, cancellationToken) {
            return providePythonSignatureHelp(model, position, cancellationToken);
        }
    });

    monaco.languages.registerHoverProvider("python", {
        provideHover(model, position, cancellationToken) {
            return providePythonHover(model, position, cancellationToken);
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
