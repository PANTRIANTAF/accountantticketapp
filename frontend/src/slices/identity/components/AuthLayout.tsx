import { useEffect, useRef, type ReactNode } from 'react';
import Box from '@mui/material/Box';
import Paper from '@mui/material/Paper';
import Stack from '@mui/material/Stack';
import Typography from '@mui/material/Typography';

/**
 * The standalone, centred frame shared by the five `shell: no` routes -- /login, /forgot-password,
 * /reset-password, /accept-invitation and /change-password (GeneralUIArchitecture.md section 4.2).
 *
 * WHY THERE IS NO NAVIGATION HERE, AND WHY THAT IS NOT AN OVERSIGHT. Someone at /login has no session
 * and therefore no role, so there is nothing to draw a nav bar from. Someone at /change-password has a
 * session the server rejects on every other route, so offering them navigation offers them ten links
 * that all 403.
 *
 * It is NOT AppShell and must not grow into it: no account menu, no notification bell, no links other
 * than the ones a screen passes as children. It lives in slices/identity/components/ rather than in
 * shared/components/ (section 1.2) because these five screens are the only ones that will ever use it
 * -- every other route in the application renders inside AppShell.
 *
 * It also carries the accessibility floor's rule 3 for these screens, the way PageHeader does for
 * shell screens: exactly one h1, focused on mount, so a route change is announced.
 */
export function AuthLayout({
  title,
  subtitle,
  children,
}: {
  title: string;
  subtitle?: ReactNode;
  children: ReactNode;
}) {
  const headingRef = useRef<HTMLHeadingElement>(null);

  useEffect(() => {
    headingRef.current?.focus();
  }, [title]);

  return (
    <Box
      component="main"
      sx={{
        minHeight: '100vh',
        display: 'flex',
        alignItems: { xs: 'flex-start', sm: 'center' },
        justifyContent: 'center',
        px: 2,
        py: { xs: 4, sm: 6 },
      }}
    >
      <Paper elevation={1} sx={{ width: '100%', maxWidth: 440, p: { xs: 3, sm: 4 } }}>
        <Stack spacing={1} sx={{ mb: 3 }}>
          <Typography variant="overline" color="text.secondary">
            AccountantApp
          </Typography>
          {/* tabIndex={-1} makes the heading focusable programmatically without adding it to the tab
              order, so the first Tab press still reaches the first field. */}
          <Typography variant="h5" component="h1" ref={headingRef} tabIndex={-1}>
            {title}
          </Typography>
          {subtitle !== undefined && (
            <Typography variant="body2" color="text.secondary">
              {subtitle}
            </Typography>
          )}
        </Stack>
        {children}
      </Paper>
    </Box>
  );
}
