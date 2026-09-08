# Integration Ops UI

React and TypeScript operator view for Integration Ops. It displays synthetic run snapshots, validates API responses and handles loading, empty and unavailable states. Reload to fetch updated status; the UI does not submit runs or poll automatically.

Use the root [`README.md`](../README.md) for project scope, setup and verification instructions.

## Commands

Run these commands from the `ui` directory. Start the API separately using the root README.

```powershell
npm ci
npm run dev
npm run lint
npm run test
npm run build
```

The development server runs at `http://localhost:5173` and proxies `/api` to the local development API at `https://localhost:7004`. It fails if port 5173 is occupied. The proxy's certificate-validation bypass is restricted to that fixed local development connection.

`npm run test` runs component and payload-parser tests with Vitest and jsdom; it does not require a running API. `npm run build` checks TypeScript and creates the static assets in `dist`. A successful build does not provide an API deployment or production hosting configuration.

**AI-assisted:** Yes
