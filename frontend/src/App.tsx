import { RouterProvider } from 'react-router-dom';
import { router } from './routes';

/**
 * <RouterProvider> and nothing else (GeneralUIArchitecture.md section 1.2, plan section 7.2).
 *
 * NO PROVIDERS HERE. They are all in main.tsx, so the router is created once, at module scope in
 * routes.tsx, and the whole provider tree is readable in one file. A provider added here would be a
 * second place to look for the answer to "what wraps what", and moving one between the two files
 * silently changes what SessionProvider can see.
 */
export function App() {
  return <RouterProvider router={router} />;
}
