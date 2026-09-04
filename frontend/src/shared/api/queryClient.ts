import { QueryClient } from '@tanstack/react-query';
import { ApiError } from './ApiError';

/**
 * GeneralUIArchitecture.md section 3.4. Not a style choice, and not to be tuned locally -- a
 * change here is a change to the governing document.
 *
 * (That document cites this file as "section 3.5" twice, in section 1.2's tree comment and in
 * section 3.2 rule F. The retry policy is section 3.4 and no section 3.5 exists: a
 * cross-reference defect in the document, not a second file to create.)
 */
export const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      // A 4xx is an answer, not a failure to get one. Retrying a 403 asks the server to deny you
      // three times and audits three denials -- PermissionChecker writes an audit row for every
      // one. Retrying a 401 delays the redirect to /login by several seconds for no gain.
      retry: (failureCount, error) =>
        error instanceof ApiError && error.status >= 500 && failureCount < 2,
      staleTime: 30_000,
      refetchOnWindowFocus: false,
    },
    // No endpoint in this API is idempotent and there is no idempotency key, so a retried
    // POST /api/employees/register creates a second Employee.
    mutations: { retry: false },
  },
});
