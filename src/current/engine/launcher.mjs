// PCBridge integration launcher: local stdio only; no setup, relay account, feature fetch or startup browser download.
import './node_modules/@wonderwhy-er/desktop-commander/dist/bootstrap.js';
import { FilteredStdioServerTransport } from './node_modules/@wonderwhy-er/desktop-commander/dist/custom-stdio.js';
import { server, flushDeferredMessages } from './node_modules/@wonderwhy-er/desktop-commander/dist/server.js';
import { configManager } from './node_modules/@wonderwhy-er/desktop-commander/dist/config-manager.js';
global.disableOnboarding = true;
await configManager.loadConfig();
const transport = new FilteredStdioServerTransport();
global.mcpTransport = transport;
server.oninitialized = () => { transport.enableNotifications(); flushDeferredMessages(); };
await server.connect(transport);