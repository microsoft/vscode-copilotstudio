import typescriptEslint from "@typescript-eslint/eslint-plugin";
import tsParser from "@typescript-eslint/parser";

export default [{
    files: ["**/*.ts"],
}, {
    plugins: {
        "@typescript-eslint": typescriptEslint,
    },
   
    languageOptions: {
        parser: tsParser,
        ecmaVersion: 2022,
        sourceType: "module",
    },

    rules: {
        "@typescript-eslint/naming-convention": ["warn", {
            selector: "import",
            format: ["camelCase", "PascalCase"],
        }],

        curly: "error",
        eqeqeq: "error",
        "no-throw-literal": "error",
        semi: "error",

        // Enforce: pass errors via { error } in data, not interpolated in the message string.
        // The logger auto-extracts errorMessage and appends details to the display message.
        "no-restricted-syntax": ["error",
            {
                // Catches: logger.logError(event, `msg: ${error.message}`) or ${(error as Error).message}
                selector: "CallExpression[callee.property.name=/^(logError|logWarning)$/] > TemplateLiteral:nth-child(2) MemberExpression[property.name='message']",
                message: "Do not interpolate error.message in logger display strings. Pass the error object via { error } in the data parameter instead."
            },
            {
                // Catches: logger.logError(event, `msg: ${error instanceof Error ? error.message : String(error)}`)
                selector: "CallExpression[callee.property.name=/^(logError|logWarning)$/] > TemplateLiteral:nth-child(2) ConditionalExpression MemberExpression[property.name='message']",
                message: "Do not interpolate error.message in logger display strings. Pass the error object via { error } in the data parameter instead."
            },
            {
                // Catches: logger.logError(event, undefined, { message: `...${error.message}...` })
                selector: "CallExpression[callee.property.name=/^(logError|logWarning)$/] > ObjectExpression Property[key.name='message'] > TemplateLiteral MemberExpression[property.name='message']",
                message: "Do not interpolate error.message in the 'message' property. Pass the error object via { error } in the data parameter instead."
            },
            {
                // Catches: logger.logError(event, undefined, { message: `...${error instanceof Error ? error.message : String(error)}...` })
                selector: "CallExpression[callee.property.name=/^(logError|logWarning)$/] > ObjectExpression Property[key.name='message'] > TemplateLiteral ConditionalExpression MemberExpression[property.name='message']",
                message: "Do not interpolate error.message in the 'message' property. Pass the error object via { error } in the data parameter instead."
            },
        ],
    },
}];