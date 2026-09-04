import Box from '@mui/material/Box';
import CircularProgress from '@mui/material/CircularProgress';
import Typography from '@mui/material/Typography';

/**
 * Centred progress INSIDE the region that is loading. GeneralUIArchitecture.md section 8.3.
 *
 * NEVER FULL-PAGE (section 5.3). There is no Backdrop, no fixed positioning and no portal here, on
 * purpose: a single top-level spinner means every navigation blanks the whole page including the nav
 * the user was about to click again. This component fills the box it is put in and nothing more --
 * when RequireSession renders it as the only content of the document, that is the document being
 * empty, not an overlay covering something.
 *
 * Lists do not use it: they render a Skeleton in the table body so the header and pager stay put and
 * the layout does not jump (section 7.4). Detail screens and the session bootstrap do.
 */
export function LoadingRegion({
  label = 'Loading…',
  minHeight = 200,
}: {
  label?: string;
  /** Layout only. Pass a viewport unit when this is the whole content region. */
  minHeight?: number | string;
}) {
  return (
    <Box
      // role="status" + aria-live="polite" so a screen reader says something during the wait.
      // Silence is indistinguishable from a hung request.
      role="status"
      aria-live="polite"
      sx={{
        minHeight,
        display: 'flex',
        flexDirection: 'column',
        alignItems: 'center',
        justifyContent: 'center',
        gap: 2,
        p: 3,
      }}
    >
      <CircularProgress aria-hidden />
      <Typography variant="body2" color="text.secondary">
        {label}
      </Typography>
    </Box>
  );
}
