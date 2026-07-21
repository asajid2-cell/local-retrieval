# Common Tasks

## How to Search Inside Conversations

1. Open the app.
2. Select `Deep search`.
3. Enter a phrase, code token, file name, or partial word.
4. Open a matching chat from the results.

Verification: the result list should show a matched snippet and an `Open chat` action.

## How to Index Local Chats

1. Open the app.
2. Wait for startup auto-detection, or open `Settings`.
3. In `Chat source`, enter the folder that contains Codex JSONL chat files, usually `%USERPROFILE%\.codex\sessions`.
4. Choose `Index folder`.

Verification: the sidebar shows your local chats, and `Settings` reports how many chats were indexed. The source folder is not modified.

## How to Copy a Restore Packet

1. Select a chat.
2. Choose `Build restore packet`.
3. Review the generated packet.
4. Use `Copy context` or the right-side quick action.

Verification: the packet includes the chat title, source path, recent context, and code blocks when present.

## How to Copy Only the Chat Path

1. Select a chat.
2. Use `Copy chat path` in the right-side actions or right-click menu.

Verification: the clipboard contains only the selected session source path.

## How to Configure an AI Provider

1. Open `Settings`.
2. In `AI providers`, choose or add an OpenAI-compatible provider.
3. Set the base URL.
4. Paste the API key and choose `Save provider`.
5. Choose `Detect models`, then select a model from the dropdown.
6. Choose `Test`.

Verification: a successful test returns a short provider response. The key is stored in Windows credentials, not the app JSON store.

## How to Ask the Archive

1. Configure an AI provider.
2. Open `Ask archive`.
3. Ask a question about a file, task, error, decision, or old code path.
4. Review the answer and linked source excerpts.

Verification: the status line reports how many local excerpts were used. The app retrieves local context first and sends only those excerpts to the provider.

## How to Package a ZIP Release

1. Run `.\tools\release\package-win-x64.ps1` from the repository root.
2. Upload `artifacts/codex-local-retrieval-win-x64.zip` as the GitHub release asset.

Verification: the ZIP contains top-level `Codex Local Retrieval.exe`, an `app` folder, and `app/CodexLocalRetrieval.Native.exe`.

## How to Keep Public Data Safe

1. Do not commit real `.codex` session files.
2. Do not commit `%LocalAppData%\CodexLocalRetrieval\app-store.json`.
3. Use sanitized fixtures under `data/fixtures/` for tests and screenshots.
4. Review all screenshots for local paths and private content before publishing.
