window.aiTextEditorBookFiles = {
    click(inputId) {
        const input = document.getElementById(inputId);
        if (!input) {
            throw new Error(`Book file input '${inputId}' was not found.`);
        }

        input.value = "";
        input.click();
    }
};
