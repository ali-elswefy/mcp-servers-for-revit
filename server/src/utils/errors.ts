/**
 * Error kinds shared with the Revit add-in. The add-in puts `kind` in JSON-RPC error data;
 * "connection" is produced here when the add-in cannot be reached at all.
 */
export type RevitErrorKind =
  | "connection"
  | "timeout"
  | "validation"
  | "revit_state"
  | "revit_unavailable"
  | "execution"
  | "user_input_required"
  | "revit_failure"
  | "invalid_request"
  | "method_not_found"
  | "internal";

export interface RevitErrorData {
  kind?: RevitErrorKind;
  /** On timeouts: "cancelled_before_start" (never ran) or "outcome_unknown" (may still change the model). */
  outcome?: "cancelled_before_start" | "outcome_unknown";
  retrySafe?: boolean;
  modelMayHaveChanged?: boolean;
  [key: string]: unknown;
}

export class RevitCommandError extends Error {
  readonly kind: RevitErrorKind;
  readonly code?: number;
  readonly data: RevitErrorData;
  readonly detail: string;

  constructor(kind: RevitErrorKind, detail: string, code?: number, data: RevitErrorData = {}) {
    // The kind is part of the message so tools that only print error.message still expose it.
    super(`[${kind}${data.outcome ? `/${data.outcome}` : ""}] ${detail}`);
    this.name = "RevitCommandError";
    this.kind = kind;
    this.code = code;
    this.data = { ...data, kind };
    this.detail = detail;
  }

  static fromJsonRpcError(error: { code?: number; message?: string; data?: RevitErrorData }): RevitCommandError {
    const data = error.data ?? {};
    return new RevitCommandError(data.kind ?? "internal", error.message || "Unknown error from Revit", error.code, data);
  }
}

export function toRevitCommandError(error: unknown): RevitCommandError {
  if (error instanceof RevitCommandError) return error;
  return new RevitCommandError("internal", error instanceof Error ? error.message : String(error));
}

/**
 * Deterministic MCP tool result for a failed Revit call. Known failure kinds are returned as
 * structured data so the agent does not need a model call to interpret them.
 */
export function revitErrorResult(operation: string, error: unknown) {
  const err = toRevitCommandError(error);
  return {
    isError: true,
    content: [
      {
        type: "text" as const,
        text: JSON.stringify(
          {
            ok: false,
            operation,
            ...(err.kind === "user_input_required" ? { stop: true, instruction: USER_INPUT_REQUIRED_INSTRUCTION } : {}),
            ...(err.kind === "revit_failure" ? { stop: true, instruction: REVIT_FAILURE_INSTRUCTION } : {}),
            error: { kind: err.kind, code: err.code, message: err.detail, ...err.data },
          },
          null,
          2
        ),
      },
    ],
  };
}

/**
 * Revit is blocked by a modal dialog (a failure dialog, a task dialog, an options window) until a
 * person answers it. Every Revit command fails the same way until then, so the agent must hand over.
 */
export const USER_INPUT_REQUIRED_INSTRUCTION =
  "Revit is waiting for the user to answer a dialog (see error.dialogs). Stop calling Revit tools, tell the user which dialog is open, " +
  "and wait for them to confirm they have dealt with it. Do not retry or work around it.";

/**
 * A Revit failure (warning or error) halted the command. The user decides how to handle it; the
 * agent passes that decision back with `failureResolutions`.
 */
export const REVIT_FAILURE_INSTRUCTION =
  "Revit reported a failure and the add-in stopped the command. Show the user each failure in error.failures: its description, " +
  "the fixes Revit offers (resolutions[].caption), whether it can be accepted (canAccept), and what was already applied (error.appliedBeforeFailure). " +
  "Ask the user which to do. Then call the same tool again, covering only what was not applied, with " +
  "failureResolutions = [{ failureId, resolution }] holding exactly the user's choice. Do not pick a fix yourself and do not retry without the user's decision.";
