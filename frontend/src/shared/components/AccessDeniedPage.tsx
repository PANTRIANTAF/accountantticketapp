import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import Typography from '@mui/material/Typography';
import { Link as RouterLink } from 'react-router-dom';

/**
 * "You do not have permission to do that." GeneralUIArchitecture.md section 7.1, row `403` without
 * `detail`; rendered by RequireRole (section 4.3) and by a detail screen whose query returned a bare
 * 403 (section 7.2).
 *
 * IT IS NOT RENDERED FOR A 404. A 404 means "not found OR not visible to you" and the backend returns
 * it for out-of-scope rows on purpose, because a 403 confirms the row exists. Rendering "no
 * permission" for a 404 both leaks that and is a lie half the time. Use NotFoundPage.
 *
 * A 403 WITH a `detail` is the forced-password-change gate, not a permission failure -- http.ts
 * catches that before any screen sees it.
 *
 * It does not offer "request access": there is no endpoint for it and no workflow behind it. The
 * honest next step is the page they can see.
 */
export function AccessDeniedPage() {
  return (
    <Box sx={{ maxWidth: 560, mx: 'auto', py: 8, textAlign: 'center' }}>
      {/* h1: this IS the page, so it owns the page's only top-level heading. Route changes move
          focus to the first heading (section 8.4 rule 3), which is this one. */}
      <Typography variant="h3" component="h1" gutterBottom tabIndex={-1}>
        You do not have permission to do that
      </Typography>
      <Typography variant="body1" color="text.secondary" sx={{ mb: 4 }}>
        Your account does not have access to this page. If you think it should, ask your accountant.
      </Typography>
      <Button component={RouterLink} to="/" variant="contained">
        Go to my start page
      </Button>
    </Box>
  );
}
