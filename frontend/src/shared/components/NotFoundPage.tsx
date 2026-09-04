import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import Typography from '@mui/material/Typography';
import { Link as RouterLink } from 'react-router-dom';

/**
 * "Not found." GeneralUIArchitecture.md section 7.1 row `404`, and the MANDATORY `*` route
 * (section 4.4).
 *
 * WHY THE `*` ROUTE IS NOT OPTIONAL. Once the three hosting lines land,
 * MapFallbackToFile("index.html") means EVERY path outside /api returns the SPA with a 200
 * (04-Infrastructure.md section 1). The server cannot tell /customers from /custmoers -- both load
 * the app. Without a `*` route a typo'd URL renders a blank page with no error in the browser and
 * none in the logs.
 *
 * "NOT FOUND" IS THE ONLY HONEST WORDING, and it is honest in both cases. A 404 from this API means
 * "not found OR not visible to you": out-of-scope rows return 404 deliberately, because a 403 would
 * confirm the row exists. So this page must never say "forbidden", "denied" or "no permission"
 * (section 2.3 rule J) -- doing so leaks the existence of a record the caller may not see, and is
 * wrong the rest of the time.
 */
export function NotFoundPage() {
  return (
    <Box sx={{ maxWidth: 560, mx: 'auto', py: 8, textAlign: 'center' }}>
      <Typography variant="h3" component="h1" gutterBottom tabIndex={-1}>
        Not found
      </Typography>
      <Typography variant="body1" color="text.secondary" sx={{ mb: 4 }}>
        We could not find that page. Check the address, or start again from your own pages.
      </Typography>
      <Button component={RouterLink} to="/" variant="contained">
        Go to my start page
      </Button>
    </Box>
  );
}
