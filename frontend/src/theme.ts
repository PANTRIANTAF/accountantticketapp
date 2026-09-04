import { createTheme } from '@mui/material/styles';

/**
 * THE ONE THEME. GeneralUIArchitecture.md section 8.1, plan section 6.1.
 *
 * One createTheme call, one ThemeProvider (main.tsx), wrapped in CssBaseline. Every colour, radius,
 * spacing step and font size comes from here.
 *
 * A HEX LITERAL IN A COMPONENT IS A DEFECT. This is an application whose look will be adjusted once,
 * globally, by somebody who will search this file and find nothing. This file is the only place in
 * the SPA that is allowed to contain one.
 *
 * `sx` is for LAYOUT LOCAL TO ONE COMPONENT -- a gap, a width, an alignment. Never for colour or
 * typography. An identical `sx` in three places is a component that should exist in
 * shared/components/.
 *
 * The semantic palette entries carry meaning that components rely on by name rather than by colour:
 * StatusChip maps a status word onto `success` / `warning` / `default`, and ErrorBanner maps a
 * status code onto `error` / `warning` / `info`. Change the colour here and every chip and banner
 * in the app follows.
 */
export const theme = createTheme({
  palette: {
    mode: 'light',
    primary: { main: '#1f4e79' },
    secondary: { main: '#4a6572' },
    // `Active` and a succeeded mutation.
    success: { main: '#2e7d32' },
    // `Invited` -- a real state, not a problem -- and a 409/429 the user can retry.
    warning: { main: '#ed6c02' },
    // A failed request. Never used to mean "this record is suspended"; see StatusChip.
    error: { main: '#c62828' },
    info: { main: '#0277bd' },
    background: { default: '#f6f7f9', paper: '#ffffff' },
  },

  shape: {
    borderRadius: 6,
  },

  typography: {
    fontFamily: [
      '"Segoe UI"',
      'Roboto',
      '-apple-system',
      'BlinkMacSystemFont',
      '"Helvetica Neue"',
      'Arial',
      'sans-serif',
    ].join(','),
    h1: { fontSize: '2rem', fontWeight: 600 },
    h2: { fontSize: '1.625rem', fontWeight: 600 },
    // Page titles use h1 semantically via PageHeader and are sized here.
    h3: { fontSize: '1.375rem', fontWeight: 600 },
    h4: { fontSize: '1.25rem', fontWeight: 600 },
    h5: { fontSize: '1.125rem', fontWeight: 600 },
    h6: { fontSize: '1rem', fontWeight: 600 },
    // SHOUTING BUTTON LABELS are MUI's default and are wrong for an application whose buttons say
    // "Change login email" rather than "OK".
    button: { textTransform: 'none', fontWeight: 600 },
  },

  components: {
    MuiAppBar: {
      defaultProps: {
        // The shell is one horizontal bar with no sidebar, so it does not need to float above
        // anything. A flat bar also keeps the nav readable against the page background.
        elevation: 0,
        color: 'primary',
      },
    },
    MuiButton: {
      defaultProps: { disableElevation: true },
    },
    MuiTextField: {
      defaultProps: {
        // Consistent field metrics everywhere, so a form does not change height when a screen
        // author forgets the prop.
        size: 'small',
        fullWidth: true,
      },
    },
    MuiTable: {
      defaultProps: { size: 'small' },
    },
    MuiTableCell: {
      styleOverrides: {
        head: { fontWeight: 600 },
      },
    },
    MuiAlert: {
      defaultProps: { variant: 'outlined' },
    },
    MuiLink: {
      defaultProps: { underline: 'hover' },
    },
  },
});
