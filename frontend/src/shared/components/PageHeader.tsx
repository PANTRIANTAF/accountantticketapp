import { useEffect, useRef, type ReactNode } from 'react';
import Box from '@mui/material/Box';
import Divider from '@mui/material/Divider';
import Stack from '@mui/material/Stack';
import Typography from '@mui/material/Typography';

/**
 * A page title, an optional subtitle, and a primary action slot.
 * GeneralUIArchitecture.md sections 5.1 and 8.3.
 *
 * THE SLOT IS WHERE can() GATES A BUTTON; THE HEADER DOES NOT GATE IT. The header knows nothing
 * about actions or roles -- a screen writes
 *
 *   <PageHeader title="Customers" action={can(role,'CreateCustomer') ? <Button…/> : undefined} />
 *
 * so the permission decision stays visible at the call site. A header that took an ActionName and
 * decided for itself would put the decision one file away from the button, and prefer-hiding-to-
 * disabling (section 6.2 rule C) would then be a property of the header rather than of the screen.
 *
 * It also does the second half of the accessibility floor's rule 3: FOCUS MOVES TO THE FIRST HEADING
 * ON ROUTE CHANGE. Every screen renders one of these as its first element, so focusing the h1 here
 * covers every route without each screen remembering to.
 *
 * No breadcrumbs (section 5.3): the route hierarchy is two levels deep at most.
 */
export function PageHeader({
  title,
  subtitle,
  action,
}: {
  title: string;
  subtitle?: ReactNode;
  action?: ReactNode;
}) {
  const headingRef = useRef<HTMLHeadingElement>(null);

  useEffect(() => {
    headingRef.current?.focus();
  }, [title]);

  return (
    <Box sx={{ mb: 3 }}>
      <Stack
        direction={{ xs: 'column', sm: 'row' }}
        spacing={2}
        sx={{ alignItems: { sm: 'center' }, justifyContent: 'space-between' }}
      >
        <Box>
          {/* Exactly one h1 per page. tabIndex={-1} makes it focusable programmatically without
              putting it in the tab order. */}
          <Typography variant="h3" component="h1" ref={headingRef} tabIndex={-1}>
            {title}
          </Typography>
          {subtitle !== undefined && (
            <Typography variant="body2" color="text.secondary" sx={{ mt: 0.5 }}>
              {subtitle}
            </Typography>
          )}
        </Box>
        {action !== undefined && <Box sx={{ flexShrink: 0 }}>{action}</Box>}
      </Stack>
      <Divider sx={{ mt: 2 }} />
    </Box>
  );
}
