import * as net from "net";
import { RevitCommandError } from "./errors.js";

/**
 * Client-side wait for one command. Must stay above the add-in's maximum external-event wait
 * (CommandTimeouts.MaximumMs = 135 s) so the add-in's structured timeout arrives first.
 */
export const DEFAULT_COMMAND_TIMEOUT_MS = 150_000;

type PendingRequest = {
  command: string;
  resolve: (value: any) => void;
  reject: (reason: RevitCommandError) => void;
  timer: NodeJS.Timeout;
};

export class RevitClientConnection {
  host: string;
  port: number;
  socket: net.Socket;
  isConnected: boolean = false;
  pendingRequests: Map<string, PendingRequest> = new Map();
  buffer: string = "";

  constructor(host: string, port: number) {
    this.host = host;
    this.port = port;
    this.socket = new net.Socket();
    this.setupSocketListeners();
  }

  private setupSocketListeners(): void {
    this.socket.on("connect", () => {
      this.isConnected = true;
    });

    this.socket.on("data", (data) => {
      // Append the received data to the buffer.
      const dataString = data.toString();
      this.buffer += dataString;

      // Try to parse a complete JSON response.
      this.processBuffer();
    });

    this.socket.on("close", () => {
      this.isConnected = false;
      this.rejectAllPending("The connection to Revit closed before a response was received.");
    });

    this.socket.on("error", (error) => {
      console.error("RevitClientConnection error:", error);
      this.isConnected = false;
      this.rejectAllPending(`Connection to Revit failed: ${error.message}`);
    });
  }

  private processBuffer(): void {
    try {
      // Try to parse the JSON.
      JSON.parse(this.buffer);
      // If parsing succeeds, handle the response and clear the buffer.
      this.handleResponse(this.buffer);
      this.buffer = "";
    } catch (e) {
      // If parsing fails, the data may be incomplete; wait for more data.
    }
  }

  public connect(): boolean {
    if (this.isConnected) {
      return true;
    }

    try {
      this.socket.connect(this.port, this.host);
      return true;
    } catch (error) {
      console.error("Failed to connect:", error);
      return false;
    }
  }

  public disconnect(): void {
    this.socket.end();
    this.isConnected = false;
  }

  private generateRequestId(): string {
    return Date.now().toString() + Math.random().toString().substring(2, 8);
  }

  private handleResponse(responseData: string): void {
    try {
      const response = JSON.parse(responseData);
      // Get the ID from the response.
      const requestId = response.id || "default";

      const pending = this.pendingRequests.get(requestId);
      if (!pending) return;

      this.pendingRequests.delete(requestId);
      clearTimeout(pending.timer);

      if (response.error) {
        pending.reject(RevitCommandError.fromJsonRpcError(response.error));
      } else {
        pending.resolve(response.result);
      }
    } catch (error) {
      console.error("Error parsing response:", error);
    }
  }

  // A dropped connection can happen after the add-in started the command, so its outcome is unknown.
  private rejectAllPending(detail: string): void {
    for (const [requestId, pending] of this.pendingRequests) {
      clearTimeout(pending.timer);
      pending.reject(
        new RevitCommandError("connection", detail, undefined, {
          command: pending.command,
          requestId,
          outcome: "outcome_unknown",
          retrySafe: false,
        })
      );
    }
    this.pendingRequests.clear();
  }

  /**
   * Sends one command. Never retries: a command that timed out or lost its connection may still
   * be running in Revit, and replaying it could duplicate model changes.
   */
  public sendCommand(
    command: string,
    params: any = {},
    timeoutMs: number = DEFAULT_COMMAND_TIMEOUT_MS
  ): Promise<any> {
    return new Promise((resolve, reject) => {
      try {
        if (!this.isConnected) {
          this.connect();
        }

        // Generate the request ID.
        const requestId = this.generateRequestId();

        // Create a JSON-RPC compliant request object.
        const commandObj = {
          jsonrpc: "2.0",
          method: command,
          params: params,
          id: requestId,
        };

        const timer = setTimeout(() => {
          if (this.pendingRequests.delete(requestId)) {
            reject(
              new RevitCommandError(
                "timeout",
                `No response from Revit for ${command} after ${timeoutMs} ms. The command may still be running; inspect the model before retrying.`,
                undefined,
                { command, requestId, timeoutMs, outcome: "outcome_unknown", retrySafe: false, source: "client" }
              )
            );
          }
        }, timeoutMs);

        this.pendingRequests.set(requestId, { command, resolve, reject, timer });

        // Send the command.
        this.socket.write(JSON.stringify(commandObj));
      } catch (error) {
        reject(error);
      }
    });
  }
}
