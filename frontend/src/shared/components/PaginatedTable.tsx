import type { ReactNode } from 'react';
import Box from '@mui/material/Box';
import LinearProgress from '@mui/material/LinearProgress';
import Paper from '@mui/material/Paper';
import Skeleton from '@mui/material/Skeleton';
import Table from '@mui/material/Table';
import TableBody from '@mui/material/TableBody';
import TableCell from '@mui/material/TableCell';
import TableContainer from '@mui/material/TableContainer';
import TableHead from '@mui/material/TableHead';
import TablePagination from '@mui/material/TablePagination';
import TableRow from '@mui/material/TableRow';
import { MAX_PAGE_SIZE, type PaginatedResponse } from '../api/paginated';
import { EmptyState } from './EmptyState';
import { ErrorBanner } from './ErrorBanner';

export interface Column<T> {
  /** Stable identity for the column. Not shown to the user. */
  key: string;
  header: ReactNode;
  render: (row: T) => ReactNode;
  align?: 'left' | 'right' | 'center';
  width?: number | string;
}

/**
 * The ONE table in the application. GeneralUIArchitecture.md sections 8.2 and 8.3.
 *
 * IT OWNS THE ONLY 1-BASED/0-BASED CONVERSION IN THE APP (section 3.3 item 3). The server's pages
 * are 1-based; MUI's TablePagination is 0-based. Two conversions is an off-by-one that silently
 * hides the first or last row of every list in the application, with no error anywhere. So:
 *
 *   - `onPageChange` hands the caller a 1-BASED pageNumber, the server's own convention, so a screen
 *     never holds a 0-based page in state and never converts anything.
 *   - the two `- 1` / `+ 1` below are the only two in the SPA.
 *
 * NO SCREEN COMPOSES Table + TablePagination ITSELF, in any slice, ever. And @mui/x-data-grid is
 * BANNED (section 8.2): every list in this API is server-paginated with a fixed envelope, DataGrid's
 * default model is client-side, and driving it server-side means four opt-out props each of which is
 * a place to get this conversion wrong or to fire a second fetch on mount.
 *
 * `rowsPerPage` COMES FROM `data.pageSize`, NEVER FROM THE VALUE SENT. PaginatedQuery.Normalize
 * CLAMPS to 50 and does not reject -- ask for 200 and you get 50 with a 200 OK. Rendering the pager
 * from what you asked for makes it compute the wrong page count, and rows go missing with no error.
 */
export function PaginatedTable<T>({
  data,
  columns,
  getRowKey,
  isLoading = false,
  isFetching = false,
  error,
  onPageChange,
  onPageSizeChange,
  emptyMessage = 'Nothing to show yet.',
  emptyDetail,
  emptyAction,
  isOverrunPage = false,
  ariaLabel,
}: {
  /** The response. `undefined` while the first request is in flight. */
  data: PaginatedResponse<T> | undefined;
  columns: readonly Column<T>[];
  /** A stable id from the row. Never the array index -- rows move between pages. */
  getRowKey: (row: T) => string;
  /** First load, no data yet: skeleton rows. */
  isLoading?: boolean;
  /** A refetch with data present: a thin progress bar, and the old rows STAY. */
  isFetching?: boolean;
  /** The query's error. Replaces the rows; the header and pager stay. */
  error?: unknown;
  /** Receives a 1-BASED pageNumber, ready to send to the API unchanged. */
  onPageChange: (pageNumber: number) => void;
  /** Omit to hide the page-size selector entirely. */
  onPageSizeChange?: (pageSize: number) => void;
  emptyMessage?: string;
  emptyDetail?: string;
  /** Already gated by can() at the call site. */
  emptyAction?: ReactNode;
  /** usePaginatedQuery's isOverrunPage. */
  isOverrunPage?: boolean;
  ariaLabel: string;
}) {
  const rows = data?.items ?? [];
  const columnCount = columns.length;

  // Never OFFER more than the server will honour. The server clamps silently, so a selector with a
  // 100 in it is a control that lies about what it did.
  const pageSizeOptions = [10, 15, 25, 50].filter((size) => size <= MAX_PAGE_SIZE);

  return (
    <Paper variant="outlined">
      {/* A refetch keeps the rows the user is reading on screen and shows progress instead of
          blanking the table (section 7.4). Blanking a table because the pager was clicked is worse
          than a brief stale row. */}
      <Box sx={{ height: 4 }}>{isFetching && !isLoading && <LinearProgress />}</Box>

      <TableContainer>
        <Table aria-label={ariaLabel}>
          <TableHead>
            <TableRow>
              {columns.map((column) => (
                <TableCell
                  key={column.key}
                  align={column.align ?? 'left'}
                  sx={column.width === undefined ? undefined : { width: column.width }}
                >
                  {column.header}
                </TableCell>
              ))}
            </TableRow>
          </TableHead>

          <TableBody>
            {error !== null && error !== undefined ? (
              <TableRow>
                <TableCell colSpan={columnCount}>
                  {/* The banner replaces the rows. focusOnMount is left on: a list that failed to
                      load is worth announcing. */}
                  <ErrorBanner error={error} />
                </TableCell>
              </TableRow>
            ) : isLoading ? (
              // Skeletons IN THE BODY, so the header and pager stay put and the layout does not
              // jump (section 7.4).
              Array.from({ length: 5 }, (_, index) => (
                <TableRow key={`skeleton-${String(index)}`}>
                  {columns.map((column) => (
                    <TableCell key={column.key}>
                      <Skeleton variant="text" />
                    </TableCell>
                  ))}
                </TableRow>
              ))
            ) : rows.length === 0 ? (
              <TableRow>
                <TableCell colSpan={columnCount}>
                  {/* Empty is not an error. And `items: []` with `totalCount > 0` is an over-run
                      page, which EmptyState turns into "back to the first page" rather than
                      "no results". */}
                  <EmptyState
                    message={emptyMessage}
                    {...(emptyDetail === undefined ? {} : { detail: emptyDetail })}
                    {...(emptyAction === undefined ? {} : { action: emptyAction })}
                    isOverrunPage={isOverrunPage}
                    onBackToFirstPage={() => onPageChange(1)}
                  />
                </TableCell>
              </TableRow>
            ) : (
              rows.map((row) => (
                <TableRow key={getRowKey(row)} hover>
                  {columns.map((column) => (
                    <TableCell key={column.key} align={column.align ?? 'left'}>
                      {column.render(row)}
                    </TableCell>
                  ))}
                </TableRow>
              ))
            )}
          </TableBody>
        </Table>
      </TableContainer>

      {data !== undefined && (
        <TablePagination
          component="div"
          count={data.totalCount}
          // CONVERSION 1 of 2: the server's 1-based pageNumber to MUI's 0-based page.
          page={Math.max(data.pageNumber - 1, 0)}
          // From the RESPONSE, never from the value sent -- the server clamps to 50 silently.
          rowsPerPage={data.pageSize}
          rowsPerPageOptions={onPageSizeChange === undefined ? [] : pageSizeOptions}
          // CONVERSION 2 of 2: MUI's 0-based page back to the server's 1-based pageNumber. The
          // caller receives a number it can put straight in the request.
          onPageChange={(_event, page) => onPageChange(page + 1)}
          onRowsPerPageChange={
            onPageSizeChange === undefined
              ? undefined
              : (event) => onPageSizeChange(Number(event.target.value))
          }
        />
      )}
    </Paper>
  );
}
