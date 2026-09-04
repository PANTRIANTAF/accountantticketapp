import Alert from '@mui/material/Alert';
import Box from '@mui/material/Box';
import Paper from '@mui/material/Paper';
import Typography from '@mui/material/Typography';

/**
 * One of the two payload panels on the entry screen -- Before, or After. AuditScreens.md section 4.3.
 *
 * THE VALUES ARE JSON *TEXT*, NOT OBJECTS. AuditEntryDetailDto declares BeforeValue/AfterValue as
 * `string?` (AuditEntryDetailDto.cs:24-25) over a jsonb column
 * (AuditRecordConfiguration.cs:21-22), and Redaction.ToJson serialises before storing. So the client
 * receives a string that may or may not parse, and pretty-printing is a display convenience that must
 * NEVER be allowed to hide the value it failed to format.
 */
export function AuditPayloadPanel({
  label,
  value,
}: {
  label: string;
  /** The raw string from the API. `null` means the action recorded no such side. */
  value: string | null;
}) {
  const payload = parsePayload(value);

  return (
    <Paper variant="outlined" sx={{ p: 2, height: '100%' }}>
      <Typography variant="subtitle2" component="h3" gutterBottom>
        {label}
      </Typography>

      {payload === null ? (
        /**
         * AN EXPLICIT SENTENCE, NEVER AN EMPTY BOX AND NEVER `{}` (section 4.3 rule D). A creation has
         * no before-state and a deletion has no after-state, which is a fact about the action; an
         * empty `{}` says "an object with no properties", which is a different claim entirely, and a
         * blank panel reads as a failed request.
         */
        <Typography variant="body2" color="text.secondary">
          {`No ${label.toLowerCase()} value — this entry records no change to existing data.`}
        </Typography>
      ) : (
        <>
          {'raw' in payload && (
            /* THE TEXT IS STILL SHOWN BELOW (section 4.3 rule C). A payload that will not parse is
               exactly the payload worth reading -- hiding it behind "could not display" destroys the
               only copy the reader has access to. */
            <Alert severity="info" sx={{ mb: 1 }}>
              This value is not valid JSON, so it is shown exactly as it was stored.
            </Alert>
          )}

          {'pretty' in payload && payload.envelope === 'truncated' && (
            /**
             * THE SERVER'S OWN MARKER, EXPLAINED (section 4.3 rule E). Redaction.cs:36 replaces a
             * payload over 8 KB with {"truncated":true,"length":n}, at WRITE time. The full value was
             * never stored, so there is nothing to expand, no "show more" to offer and no request
             * that would fetch the rest.
             */
            <Alert severity="warning" sx={{ mb: 1 }}>
              The value was larger than the 8 KB the audit log stores, so only this marker was
              recorded. The original content is not available anywhere.
            </Alert>
          )}

          {'pretty' in payload && payload.envelope === 'unserialisable' && (
            /* Redaction.cs:59-63: serialisation itself failed when the entry was written, and the
               type name is all that was kept. Again a write-time fact, not a client-side one. */
            <Alert severity="warning" sx={{ mb: 1 }}>
              The value could not be serialised when the entry was written, so only its type was
              recorded.
            </Alert>
          )}

          {'pretty' in payload && payload.hasRedaction && (
            /**
             * "[redacted]" IS RENDERED LITERALLY AND THERE IS NO UNREDACT AFFORDANCE (section 4.3
             * rule F). Redaction.cs:21-24 substitutes the placeholder before the row is inserted:
             * password, token, secret and hash values never existed in the database. A "reveal"
             * control -- even a disabled one -- would imply an endpoint that must never exist.
             */
            <Alert severity="info" sx={{ mb: 1 }}>
              Any value shown as [redacted] was removed before this entry was stored. Redaction
              happens at write time, so the original was never recorded and cannot be recovered.
            </Alert>
          )}

          {/*
            THE JSON IS A TEXT CHILD OF A <pre>, NEVER dangerouslySetInnerHTML (section 4.3 rule B).
            These payloads contain user-supplied strings -- customer names, ticket titles -- and the
            audit log is the one screen an attacker most wants to run script on, because it is only
            ever read by an AccountantAdmin.

            AND IT IS NEVER TRUNCATED HERE. The scroll box bounds the LAYOUT; the reader can always
            reach every character the server stored. A client-side cut would be indistinguishable from
            the server's own truncation marker above.
          */}
          <Box
            component="pre"
            sx={{
              m: 0,
              p: 1.5,
              maxHeight: 400,
              overflow: 'auto',
              bgcolor: 'action.hover',
              borderRadius: 1,
              fontFamily: 'monospace',
              fontSize: '0.8125rem',
              whiteSpace: 'pre-wrap',
              overflowWrap: 'anywhere',
            }}
          >
            {'pretty' in payload ? payload.pretty : payload.raw}
          </Box>
        </>
      )}
    </Paper>
  );
}

/** What the raw string turned out to be. */
type ParsedPayload =
  | {
      /** Formatted for reading. */
      pretty: string;
      /** Which of the server's two write-time markers this is, if either. */
      envelope: 'truncated' | 'unserialisable' | null;
      /** Whether the payload carries Redaction's placeholder anywhere inside it. */
      hasRedaction: boolean;
    }
  | { raw: string };

/**
 * THREE OUTCOMES, AND THE THIRD IS NOT AN ERROR:
 *
 *   { pretty }  it parsed; show it indented, with any write-time marker explained.
 *   { raw }     it did not parse; show the stored text verbatim, with a note.
 *   null        there is nothing on this side of the change.
 *
 * A parse failure is NOT rendered as an error and never replaces the value. jsonb guarantees the
 * column holds valid JSON, so this branch means either a row written before the column was jsonb or
 * a value the client cannot format -- in both cases the stored text is the evidence.
 *
 * TWO THINGS THAT LOOK LIKE null ARE NOT null (plan section 7 rule C):
 *
 *   ''      an empty or whitespace-only string fails JSON.parse and lands in { raw }, which is
 *           correct: "the column holds something this UI could not parse" and "the column is null"
 *           are different facts about the row, and only the second means no change was recorded.
 *   'null'  the four-character JSON document is a VALUE. Redaction.ToJson writes it when
 *           serialisation produced a JSON null (Redaction.cs:33); it parses, so it prints as null.
 *           The no-change sentence would claim the column was empty when it holds something.
 */
export function parsePayload(raw: string | null): ParsedPayload | null {
  if (raw === null) return null;

  let parsed: unknown;
  try {
    parsed = JSON.parse(raw);
  } catch {
    return { raw };
  }

  // The literal string "null" is NOT nothing. Redaction.ToJson writes `?? "null"` (Redaction.cs:33),
  // so a recorded null value arrives as valid JSON meaning "this was null" -- a different fact from
  // "no value was recorded", which is the null above. It is printed as null.
  return {
    pretty: JSON.stringify(parsed, null, 2),
    envelope: envelopeOf(parsed),
    hasRedaction: raw.includes('[redacted]'),
  };
}

function envelopeOf(parsed: unknown): 'truncated' | 'unserialisable' | null {
  if (typeof parsed !== 'object' || parsed === null) return null;
  const record = parsed as Record<string, unknown>;
  if (record['truncated'] === true) return 'truncated';
  if (record['unserialisable'] === true) return 'unserialisable';
  return null;
}
