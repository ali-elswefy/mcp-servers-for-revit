import { RevitClientConnection } from "./SocketClient.js";
import { RevitCommandError } from "./errors.js";

// Mutex to serialize all Revit connections - prevents race conditions
// when multiple requests are made in parallel
let connectionMutex: Promise<void> = Promise.resolve();

const REVIT_HOST = "localhost";
const REVIT_PORT = 8080;
const CONNECT_TIMEOUT_MS = 5000;

function connectionError(detail: string): RevitCommandError {
  // Nothing was sent, so the request never reached Revit and is safe to retry.
  return new RevitCommandError("connection", detail, undefined, {
    host: REVIT_HOST,
    port: REVIT_PORT,
    executed: false,
    retrySafe: true,
  });
}

/**
 * Connect to the Revit client and run an operation.
 * @param operation Function to run after a successful connection.
 * @param options.exclusive Wait for earlier operations to finish first (default). Pass false for
 *   read-only diagnostics such as health_check, which must not queue behind a long-running command.
 * @returns The operation result.
 */
export async function withRevitConnection<T>(
  operation: (client: RevitClientConnection) => Promise<T>,
  options: { exclusive?: boolean } = {}
): Promise<T> {
  const exclusive = options.exclusive ?? true;

  // Wait for any pending connection to complete before starting a new one
  let releaseMutex: () => void = () => {};
  if (exclusive) {
    const previousMutex = connectionMutex;
    connectionMutex = new Promise<void>((resolve) => {
      releaseMutex = resolve;
    });
    await previousMutex;
  }

  const revitClient = new RevitClientConnection(REVIT_HOST, REVIT_PORT);

  try {
    // Connect to the Revit client
    if (!revitClient.isConnected) {
      await new Promise<void>((resolve, reject) => {
        const cleanup = () => {
          clearTimeout(timer);
          revitClient.socket.removeListener("connect", onConnect);
          revitClient.socket.removeListener("error", onError);
        };

        const onConnect = () => {
          cleanup();
          resolve();
        };

        const onError = (error: Error) => {
          cleanup();
          reject(
            connectionError(
              `Failed to connect to the Revit add-in at ${REVIT_HOST}:${REVIT_PORT} (${error.message}). Check that Revit is open and the MCP service is switched on.`
            )
          );
        };

        const timer = setTimeout(() => {
          cleanup();
          reject(
            connectionError(
              `Timed out after ${CONNECT_TIMEOUT_MS} ms connecting to the Revit add-in at ${REVIT_HOST}:${REVIT_PORT}.`
            )
          );
        }, CONNECT_TIMEOUT_MS);

        revitClient.socket.on("connect", onConnect);
        revitClient.socket.on("error", onError);

        revitClient.connect();
      });
    }

    // Run the operation
    return await operation(revitClient);
  } finally {
    // Disconnect
    revitClient.disconnect();
    // Release the mutex so the next request can proceed
    releaseMutex();
  }
}
