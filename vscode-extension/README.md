# DeoLoadTest for Visual Studio Code

Initial TypeScript extension bootstrap for DeoLoadTest.

## Requirements

- Visual Studio Code 1.90 or later
- Node.js and npm

## Build

From this directory:

```bash
npm install
npm run compile
```

## Run in the Extension Development Host

1. Open this directory in Visual Studio Code.
2. Press `F5` to launch the Extension Development Host.
3. Open the Command Palette.
4. Run **DeoLoadTest: Show Welcome**.

The command confirms that the extension has been activated.

## Current scope

This bootstrap intentionally does not start `LoadTest.VSCode.Host` yet. Host process management and JSON-RPC communication are the next implementation steps.
