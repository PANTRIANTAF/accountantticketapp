import { useState, type MouseEvent, type ReactNode } from 'react';
import AppBar from '@mui/material/AppBar';
import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import Container from '@mui/material/Container';
import Divider from '@mui/material/Divider';
import IconButton from '@mui/material/IconButton';
import ListItemIcon from '@mui/material/ListItemIcon';
import ListItemText from '@mui/material/ListItemText';
import Menu from '@mui/material/Menu';
import MenuItem from '@mui/material/MenuItem';
import Toolbar from '@mui/material/Toolbar';
import Typography from '@mui/material/Typography';
import useMediaQuery from '@mui/material/useMediaQuery';
import { useTheme } from '@mui/material/styles';
import AccountCircleIcon from '@mui/icons-material/AccountCircle';
import LogoutIcon from '@mui/icons-material/Logout';
import MenuIcon from '@mui/icons-material/Menu';
import PersonIcon from '@mui/icons-material/Person';
import { Link as RouterLink, Outlet, useLocation } from 'react-router-dom';
import { useAuthenticatedSession } from '../auth/useSession';
import { ROLE_LABELS, UserRole } from '../format/enums';

/**
 * GeneralUIArchitecture.md section 5.1's layout, exactly:
 *
 *   +------------------------------------------------------------------+
 *   | AccountantApp   [ Customers  Employees  Ticket types  Audit ]    |  AppBar
 *   |                                     [bell 3]  Jane Doe (AA) v    |
 *   +------------------------------------------------------------------+
 *   |   <PageHeader />                                                 |
 *   |   <Outlet />                                                     |  content
 *   +------------------------------------------------------------------+
 *
 * ONE HORIZONTAL AppBar, NO SIDEBAR. There are seven nav destinations at most and four for any
 * single role; a collapsible drawer for four links is machinery with nothing to manage. On small
 * screens the nav collapses into a Menu behind an icon button.
 *
 * The account menu shows the display name AND THE ROLE, and offers Profile and Sign out. The role is
 * shown because two people at the same Customer can see different buttons, and "why can she suspend
 * and I cannot" is otherwise unanswerable from the screen.
 *
 * WHAT THE SHELL DOES NOT DO, AND WHY (section 5.3):
 *
 *   - No global loading spinner. Every navigation would blank the whole page including the nav the
 *     user was about to click again. Loading renders inside the region that is loading.
 *   - No global error toast for query failures. An error belongs next to the thing that failed. A
 *     toast is dismissible, unlocatable, and gone before it has been read. Toasts are for SUCCESSES.
 *   - No breadcrumbs. The route hierarchy is two levels deep at most.
 *   - No client-side search across slices. Every list is server-paginated and there is no endpoint.
 *   - No permission fetching (section 6.3).
 *   - NO SESSION POLLING. It does not check /api/auth/me on a timer. The cookie slides on every
 *     request, so polling to detect expiry would PREVENT the expiry it was watching for. Expiry is
 *     detected like any other failure: the next call 401s and http.ts fires.
 */
export function AppShell({
  onSignOut,
  isSigningOut = false,
  notificationSlot,
}: {
  /**
   * Sign out. SUPPLIED BY routes.tsx, not implemented here: logout lives in
   * slices/identity/queries.ts as useLogout (it clears the WHOLE query cache and navigates with
   * `replace`), and shared/ may never import from slices/ (section 1.4 rule A). Reimplementing it
   * here would be a second logout to keep in step with the first.
   */
  onSignOut: () => void;
  isSigningOut?: boolean;
  /**
   * SEAM B (Plans/README.md). The Notifications slice mounts its unread badge here. shared/ may not
   * import from slices/, so the badge cannot be imported into this file; instead routes.tsx -- which
   * already imports every slice's screens -- passes <UnreadBadge /> in. The coupling lands in the
   * file built to hold it.
   */
  notificationSlot?: ReactNode;
}) {
  const { displayName, role } = useAuthenticatedSession();
  const theme = useTheme();
  const isSmall = useMediaQuery(theme.breakpoints.down('md'));
  const location = useLocation();

  const [navAnchor, setNavAnchor] = useState<HTMLElement | null>(null);
  const [accountAnchor, setAccountAnchor] = useState<HTMLElement | null>(null);

  const items = NAV_ITEMS.filter((item) => item.roles.includes(role));

  const openNav = (event: MouseEvent<HTMLElement>) => setNavAnchor(event.currentTarget);
  const openAccount = (event: MouseEvent<HTMLElement>) => setAccountAnchor(event.currentTarget);
  const closeNav = () => setNavAnchor(null);
  const closeAccount = () => setAccountAnchor(null);

  const isCurrent = (route: string) =>
    location.pathname === route || location.pathname.startsWith(`${route}/`);

  return (
    <Box sx={{ display: 'flex', flexDirection: 'column', minHeight: '100vh' }}>
      <AppBar position="static">
        <Toolbar sx={{ gap: 1 }}>
          {isSmall && (
            <>
              <IconButton
                edge="start"
                color="inherit"
                onClick={openNav}
                // Icon-only buttons carry an aria-label (section 8.4 rule 4).
                aria-label="Open navigation"
                aria-haspopup="true"
              >
                <MenuIcon />
              </IconButton>
              <Menu anchorEl={navAnchor} open={navAnchor !== null} onClose={closeNav}>
                {items.map((item) => (
                  <MenuItem
                    key={item.route}
                    component={RouterLink}
                    to={item.route}
                    onClick={closeNav}
                    selected={isCurrent(item.route)}
                  >
                    {item.label}
                  </MenuItem>
                ))}
              </Menu>
            </>
          )}

          <Typography
            variant="h6"
            component={RouterLink}
            to="/"
            sx={{ color: 'inherit', textDecoration: 'none', mr: 2 }}
          >
            AccountantApp
          </Typography>

          {!isSmall && (
            <Box component="nav" aria-label="Main" sx={{ display: 'flex', gap: 0.5 }}>
              {items.map((item) => (
                <Button
                  key={item.route}
                  component={RouterLink}
                  to={item.route}
                  color="inherit"
                  // aria-current is how a screen reader knows which page it is on. Colour and an
                  // underline are not enough on their own (section 8.4).
                  aria-current={isCurrent(item.route) ? 'page' : undefined}
                  sx={{
                    textDecoration: isCurrent(item.route) ? 'underline' : 'none',
                    textUnderlineOffset: 6,
                  }}
                >
                  {item.label}
                </Button>
              ))}
            </Box>
          )}

          <Box sx={{ flexGrow: 1 }} />

          {notificationSlot}

          <Button
            color="inherit"
            onClick={openAccount}
            startIcon={<AccountCircleIcon />}
            aria-haspopup="true"
            aria-label={`Account menu for ${displayName}, ${ROLE_LABELS[role]}`}
          >
            {/* The role is part of the visible label, not only the aria-label: see the note above.
                ROLE_LABELS is the only source of role text -- never String(role), and never the
                bare word "Admin", which is ambiguous between AccountantAdmin and CustomerAdmin. */}
            <Box component="span" sx={{ display: { xs: 'none', sm: 'inline' } }}>
              {displayName} ({ROLE_LABELS[role]})
            </Box>
          </Button>

          <Menu anchorEl={accountAnchor} open={accountAnchor !== null} onClose={closeAccount}>
            {/* Repeated inside the menu so the identity is readable on a small screen, where the
                button label is hidden. */}
            <Box sx={{ px: 2, py: 1 }}>
              <Typography variant="subtitle2">{displayName}</Typography>
              <Typography variant="caption" color="text.secondary">
                {ROLE_LABELS[role]}
              </Typography>
            </Box>
            <Divider />
            <MenuItem component={RouterLink} to="/profile" onClick={closeAccount}>
              <ListItemIcon>
                <PersonIcon fontSize="small" />
              </ListItemIcon>
              <ListItemText>Profile</ListItemText>
            </MenuItem>
            <MenuItem
              onClick={() => {
                closeAccount();
                onSignOut();
              }}
              disabled={isSigningOut}
            >
              <ListItemIcon>
                <LogoutIcon fontSize="small" />
              </ListItemIcon>
              <ListItemText>Sign out</ListItemText>
            </MenuItem>
          </Menu>
        </Toolbar>
      </AppBar>

      <Container component="main" maxWidth="lg" sx={{ py: 4, flexGrow: 1 }}>
        <Outlet />
      </Container>
    </Box>
  );
}

/**
 * GeneralUIArchitecture.md section 5.2, transcribed row for row, in order.
 *
 * THE NAV IS DERIVED FROM THIS TABLE AND THE SESSION'S ROLE. IT IS NOT DERIVED FROM can(). A nav
 * item maps to a PAGE, not to an action, and several pages combine actions with different role sets:
 * /employees is visible to a CustomerAdmin who may list employees but may not onboard a Customer.
 *
 * An EMPLOYEE GETS THREE ITEMS -- My Customer, Ticket types, Notifications. That is correct and not a
 * gap to pad out (section 12 item 2): an Employee's real home is "my tickets" and the Tickets UI does
 * not exist. Do not invent a fourth.
 *
 * In Phase 0 the destinations that do not exist yet resolve to the `*` route. Six slice plans replace
 * those route elements; none of them touches this table.
 */
const NAV_ITEMS: readonly { label: string; route: string; roles: readonly UserRole[] }[] = [
  {
    label: 'Customers',
    route: '/customers',
    roles: [UserRole.AccountantAdmin, UserRole.AccountantUser],
  },
  {
    label: 'My Customer',
    route: '/my-customer',
    roles: [UserRole.CustomerAdmin, UserRole.Employee],
  },
  {
    label: 'Employees',
    route: '/employees',
    roles: [UserRole.AccountantAdmin, UserRole.AccountantUser, UserRole.CustomerAdmin],
  },
  {
    label: 'Ticket types',
    route: '/ticket-types',
    roles: [
      UserRole.AccountantAdmin,
      UserRole.AccountantUser,
      UserRole.CustomerAdmin,
      UserRole.Employee,
    ],
  },
  {
    label: 'Accountants',
    route: '/accountants',
    roles: [UserRole.AccountantAdmin, UserRole.AccountantUser],
  },
  // AccountantAdmin ONLY. An AccountantUser must not see an Audit log item.
  { label: 'Audit log', route: '/audit', roles: [UserRole.AccountantAdmin] },
  {
    label: 'Notifications',
    route: '/notifications',
    roles: [
      UserRole.AccountantAdmin,
      UserRole.AccountantUser,
      UserRole.CustomerAdmin,
      UserRole.Employee,
    ],
  },
];
