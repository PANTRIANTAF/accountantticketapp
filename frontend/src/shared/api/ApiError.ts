export class ApiError extends Error {
  constructor(
    readonly status: number,
    /** The human-readable message. The API puts it in `title`, not `detail`. See rule F. */
    readonly title: string,
    /** Populated by exactly one response in the whole API: the must-change-password 403. */
    readonly detail: string | undefined,
    readonly traceId: string | undefined,
  ) {
    super(title);
    this.name = 'ApiError';
  }

  /** True for the forced-password-change gate. See LoginArchitecture.md section 3. */
  get isPasswordChangeRequired(): boolean {
    return this.status === 403 && this.detail !== undefined;
  }
}
