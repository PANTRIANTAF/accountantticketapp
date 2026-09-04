import * as z from 'zod';
import { UserRole } from '../../../shared/format/enums';

/**
 * The invite form's three rules, mirrored EXACTLY from the server. Stricter blocks input the server
 * would accept; looser produces the unattached banner GeneralUIArchitecture.md section 7.3 describes.
 *
 * The file lives under screens/ rather than beside the dialog because IdentityScreens.md's files
 * checklist puts it there, and that spec outranks the plan.
 *
 *   email        trim, required, <= 320    EmailNormalization.cs:13, :33-37
 *   email        exactly one '@', not at either end
 *                                          EmailNormalization.cs:38-40
 *   displayName  trim, required, <= 200    InviteAccountantHandler.cs:19, :65-70
 *   role         AccountantAdmin or AccountantUser, as a NUMBER
 *                                          InviteAccountantHandler.cs:58-60
 *
 * A. NO EMAIL REGEX. EmailNormalization.cs:23-27 says why: one '@' with something on both sides, then
 *    System.Net.Mail.MailAddress parses it, "deliberately not a regular expression: an over-clever
 *    pattern rejects legitimate addresses, and the invitation email is the real validator". A stricter
 *    client pattern rejects addresses the server accepts and nothing tells the user which of the two
 *    rules is the imaginary one.
 * B. DO NOT LOWERCASE THE EMAIL. InviteAccountantHandler.cs:91-92 keeps LoginEmail as typed and
 *    normalises separately, into a different column, for uniqueness only. Lowercasing here changes what
 *    is displayed in the list and what is mailed.
 * C. 200 FOR THE DISPLAY NAME, NOT 255. Most display names elsewhere in this API are capped at 255;
 *    mirroring 255 here produces a 422 about a limit the user appears to be within. The two constants
 *    are declared here rather than reused from types.ts because they are two INDEPENDENT server
 *    constants that happen to be equal today.
 * D. THE MESSAGES ARE THE SERVER'S OWN WORDING, so a client rejection and a server 422 for the same
 *    mistake do not read as two different rules.
 */
const EMAIL_MAX_LENGTH = 320;
const DISPLAY_NAME_MAX_LENGTH = 200;

export const inviteAccountantSchema = z.object({
  email: z
    .string()
    // Rule from section 9.3 rule E: trim before submitting, or a trailing space pushes a
    // 320-character address to 321 and the 422 names a limit the user appears to be within.
    .trim()
    .min(1, 'An email address is required.')
    .max(EMAIL_MAX_LENGTH, `The email address must be at most ${String(EMAIL_MAX_LENGTH)} characters long.`)
    // Rule A: the server's structural check and nothing more.
    .refine(
      (value) => value.split('@').length === 2 && !value.startsWith('@') && !value.endsWith('@'),
      'That email address is not valid.',
    ),
  displayName: z
    .string()
    .trim()
    .min(1, 'A display name is required.')
    .max(
      DISPLAY_NAME_MAX_LENGTH,
      `The display name must be at most ${String(DISPLAY_NAME_MAX_LENGTH)} characters long.`,
    ),
  /**
   * TWO OPTIONS AND ONLY TWO, as NUMBERS. InviteAccountantHandler.cs:58-60 answers 422 "An invited
   * accountant must be an Accountant Admin or an Accountant User." for CustomerAdmin and Employee,
   * because a Customer-side row needs the employee_id and customer_id this endpoint cannot supply.
   * A four-option picker would offer two choices that can only ever fail.
   */
  role: z.union([z.literal(UserRole.AccountantAdmin), z.literal(UserRole.AccountantUser)]),
});

export type InviteAccountantFormValues = z.infer<typeof inviteAccountantSchema>;
