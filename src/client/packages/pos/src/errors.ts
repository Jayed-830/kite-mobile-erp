/** A refusal with the same code the server would give. */
export class PosError extends Error {
  constructor(readonly code: string, message: string) { super(message); }
}
