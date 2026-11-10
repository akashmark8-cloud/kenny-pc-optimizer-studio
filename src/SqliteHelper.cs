using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace KennyPCOptimizer
{
    public static class SqliteHelper
    {
        private const int SQLITE_OK = 0;
        private const int SQLITE_ROW = 100;
        private const int SQLITE_OPEN_READONLY = 0x00000001;
        private const int SQLITE_OPEN_READWRITE = 0x00000002;

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr zVfs);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_close(IntPtr db);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_busy_timeout(IntPtr db, int ms);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_prepare_v2(IntPtr db, byte[] zSql, int nByte, out IntPtr ppStmt, IntPtr pzTail);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_step(IntPtr stmt);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_finalize(IntPtr stmt);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern long sqlite3_column_int64(IntPtr stmt, int iCol);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr sqlite3_column_text(IntPtr stmt, int iCol);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_exec(IntPtr db, byte[] sql, IntPtr callback, IntPtr arg, out IntPtr errmsg);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void sqlite3_free(IntPtr ptr);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr sqlite3_errmsg(IntPtr db);

        private static byte[] ToUtf8Z(string s)
        {
            return Encoding.UTF8.GetBytes(s + "\0");
        }

        private static string PtrToUtf8(IntPtr ptr)
        {
            if (ptr == IntPtr.Zero) return "";
            int len = 0;
            while (Marshal.ReadByte(ptr, len) != 0) len++;
            byte[] buf = new byte[len];
            Marshal.Copy(ptr, buf, 0, len);
            return Encoding.UTF8.GetString(buf);
        }

        private static long QueryScalarLong(IntPtr db, string sql)
        {
            IntPtr stmt;
            if (sqlite3_prepare_v2(db, ToUtf8Z(sql), -1, out stmt, IntPtr.Zero) != SQLITE_OK) return 0;
            try
            {
                if (sqlite3_step(stmt) == SQLITE_ROW) return sqlite3_column_int64(stmt, 0);
                return 0;
            }
            finally
            {
                sqlite3_finalize(stmt);
            }
        }

        private static HashSet<string> GetTables(IntPtr db)
        {
            HashSet<string> tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            IntPtr stmt;
            if (sqlite3_prepare_v2(db, ToUtf8Z("SELECT name FROM sqlite_master WHERE type='table'"), -1, out stmt, IntPtr.Zero) != SQLITE_OK)
                return tables;
            try
            {
                while (sqlite3_step(stmt) == SQLITE_ROW)
                {
                    tables.Add(PtrToUtf8(sqlite3_column_text(stmt, 0)));
                }
            }
            finally
            {
                sqlite3_finalize(stmt);
            }
            return tables;
        }

        private static bool ExecSql(IntPtr db, string sql, out string errMsg)
        {
            errMsg = "";
            IntPtr errPtr;
            int rc = sqlite3_exec(db, ToUtf8Z(sql), IntPtr.Zero, IntPtr.Zero, out errPtr);
            if (errPtr != IntPtr.Zero)
            {
                errMsg = PtrToUtf8(errPtr);
                sqlite3_free(errPtr);
            }
            return rc == SQLITE_OK;
        }

        public static void InspectGeckoPlaces(string placesPath, out long visits, out long bookmarks)
        {
            visits = 0;
            bookmarks = 0;
            if (!File.Exists(placesPath)) return;
            try
            {
                IntPtr db;
                if (sqlite3_open_v2(ToUtf8Z(placesPath), out db, SQLITE_OPEN_READONLY, IntPtr.Zero) != SQLITE_OK)
                {
                    if (db != IntPtr.Zero) sqlite3_close(db);
                    return;
                }
                try
                {
                    sqlite3_busy_timeout(db, 1000);
                    HashSet<string> tables = GetTables(db);
                    if (tables.Contains("moz_historyvisits"))
                        visits = QueryScalarLong(db, "SELECT COUNT(*) FROM moz_historyvisits");
                    if (tables.Contains("moz_bookmarks"))
                        bookmarks = QueryScalarLong(db, "SELECT COUNT(*) FROM moz_bookmarks WHERE fk IS NOT NULL");
                }
                finally
                {
                    sqlite3_close(db);
                }
            }
            catch { }
        }

        public static Dictionary<string, object> ScrubGeckoPlacesPreservingBookmarks(string placesPath, bool dryRun)
        {
            if (!File.Exists(placesPath))
            {
                return new Dictionary<string, object>
                {
                    { "ok", true }, { "visits_removed", 0L }, { "bookmarks_preserved", 0L }, { "freed_bytes", 0L }
                };
            }

            long sizeBefore = new FileInfo(placesPath).Length;
            if (dryRun)
            {
                long v, b;
                InspectGeckoPlaces(placesPath, out v, out b);
                return new Dictionary<string, object>
                {
                    { "ok", true }, { "visits_removed", v }, { "bookmarks_preserved", b }, { "freed_bytes", 0L }, { "dry_run", true }
                };
            }

            IntPtr db = IntPtr.Zero;
            try
            {
                if (sqlite3_open_v2(ToUtf8Z(placesPath), out db, SQLITE_OPEN_READWRITE, IntPtr.Zero) != SQLITE_OK)
                {
                    string msg = db != IntPtr.Zero ? PtrToUtf8(sqlite3_errmsg(db)) : "Unable to open database";
                    if (db != IntPtr.Zero) sqlite3_close(db);
                    return new Dictionary<string, object>
                    {
                        { "ok", false }, { "locked", true }, { "error", msg }, { "visits_removed", 0L }, { "freed_bytes", 0L }
                    };
                }

                sqlite3_busy_timeout(db, 1500);
                HashSet<string> tables = GetTables(db);
                long visitsBefore = tables.Contains("moz_historyvisits") ? QueryScalarLong(db, "SELECT COUNT(*) FROM moz_historyvisits") : 0;
                long bookmarksCount = tables.Contains("moz_bookmarks") ? QueryScalarLong(db, "SELECT COUNT(*) FROM moz_bookmarks WHERE fk IS NOT NULL") : 0;

                string err;
                if (!ExecSql(db, "BEGIN IMMEDIATE;", out err))
                {
                    return new Dictionary<string, object>
                    {
                        { "ok", false }, { "locked", true }, { "error", err }, { "visits_removed", 0L }, { "freed_bytes", 0L }
                    };
                }

                string[] histTables = new string[]
                {
                    "moz_historyvisits_extra",
                    "moz_historyvisits",
                    "moz_inputhistory",
                    "moz_places_metadata",
                    "moz_places_metadata_search_queries"
                };
                foreach (string tbl in histTables)
                {
                    if (tables.Contains(tbl))
                    {
                        if (!ExecSql(db, "DELETE FROM " + tbl + ";", out err))
                        {
                            string rbErr;
                            ExecSql(db, "ROLLBACK;", out rbErr);
                            return new Dictionary<string, object>
                            {
                                { "ok", false }, { "locked", true }, { "error", err }, { "visits_removed", 0L }, { "freed_bytes", 0L }
                            };
                        }
                    }
                }

                if (tables.Contains("moz_places") && tables.Contains("moz_bookmarks"))
                {
                    ExecSql(db,
                        "DELETE FROM moz_places WHERE id NOT IN (SELECT DISTINCT fk FROM moz_bookmarks WHERE fk IS NOT NULL) AND COALESCE(foreign_count, 0) = 0;",
                        out err);
                    ExecSql(db, "UPDATE moz_places SET visit_count = 0, last_visit_date = NULL, typed = 0;", out err);
                }

                if (tables.Contains("moz_origins") && tables.Contains("moz_places"))
                {
                    ExecSql(db,
                        "DELETE FROM moz_origins WHERE id NOT IN (SELECT DISTINCT origin_id FROM moz_places WHERE origin_id IS NOT NULL);",
                        out err);
                }

                ExecSql(db, "COMMIT;", out err);
                ExecSql(db, "VACUUM;", out err);
                sqlite3_close(db);
                db = IntPtr.Zero;

                long sizeAfter = new FileInfo(placesPath).Length;
                return new Dictionary<string, object>
                {
                    { "ok", true },
                    { "visits_removed", visitsBefore },
                    { "bookmarks_preserved", bookmarksCount },
                    { "freed_bytes", Math.Max(0L, sizeBefore - sizeAfter) }
                };
            }
            catch (Exception ex)
            {
                if (db != IntPtr.Zero) try { sqlite3_close(db); } catch { }
                return new Dictionary<string, object>
                {
                    { "ok", false }, { "locked", false }, { "error", ex.Message }, { "visits_removed", 0L }, { "freed_bytes", 0L }
                };
            }
        }
    }
}
