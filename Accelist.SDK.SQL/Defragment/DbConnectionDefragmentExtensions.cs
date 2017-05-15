﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Dapper;
using Serilog;

namespace Accelist.SDK.SQL.Defragment
{
    /// <summary>
    /// Provides extension methods for defragmenting SQL Server database.
    /// </summary>
    public static class DbConnectionDefragmentExtensions
    {
        /// <summary>
        /// Return fragmentation scan result on all tables in the database.
        /// </summary>
        /// <param name="db"></param>
        /// <returns></returns>
        public static async Task<IEnumerable<IndexScan>> FragmentationScanAsync(this IDbConnection db)
        {
            var stopwatch = Stopwatch.StartNew();

            // First do a full-database index scan
            var commandText = @"SELECT DISTINCT
    s.name as [Schema],
    t.name as [Table],
    ind.name as [Index],
    indexstats.page_count as [PageCount],
    indexstats.avg_fragmentation_in_percent as [FragmentRate],
    ind.fill_factor as [FillFactor],
    col.is_identity as [IsIdentity]
FROM sys.dm_db_index_physical_stats(DB_ID(), NULL, NULL, NULL, 'DETAILED') indexstats 
JOIN sys.indexes ind ON indexstats.object_id = ind.object_id and indexstats.index_id = ind.index_id
JOIN sys.index_columns ic ON  ind.object_id = ic.object_id and ind.index_id = ic.index_id 
JOIN sys.columns col ON ic.object_id = col.object_id and ic.column_id = col.column_id 
JOIN sys.tables t ON ind.object_id = t.object_id 
JOIN sys.schemas s on s.schema_id = t.schema_id
WHERE t.is_ms_shipped = 0 AND NOT (t.name = 'sysdiagrams')";
            var scans = await db.QueryAsync<IndexScan>(commandText);

            /*
            AND indexstats.page_count > 1000
            AND indexstats.avg_fragmentation_in_percent > 10
            */

            Log.Information("Scanning SQL Server database {DatabaseName} index fragmentations: ({ElapsedMilliseconds} ms) {CommandText} {Scans}",
                db.Database, commandText, scans, stopwatch.ElapsedMilliseconds);
            return scans;
        }


        /// <summary>
        /// Plan defragment commands, using analysis on index fragmentation scans from the database.
        /// Uses online rebuild when rebuilding if enabled through method parameter.
        /// </summary>
        /// <param name="db"></param>
        /// <param name="onlineRebuild"></param>
        /// <returns></returns>
        public static async Task<List<string>> PlanDefragmentAsync(this IDbConnection db, bool onlineRebuild = false)
        {
            var scans = await db.FragmentationScanAsync();
            var fragments = scans.Where(Q => Q.PageCount > 1000 && Q.FragmentRate > 10);
            var commands = new List<string>();

            foreach (var fragment in fragments)
            {
                if (fragment.FragmentRate > 30)
                {
                    // rebuild
                    var ff = fragment.CalculateFillFactor();
                    if (onlineRebuild)
                    {
                        commands.Add($"ALTER INDEX [{fragment.Index}] ON [{fragment.Schema}].[{fragment.Table}] REBUILD WITH (FILLFACTOR = {ff}, ONLINE = ON)");
                    }
                    else
                    {
                        commands.Add($"ALTER INDEX [{fragment.Index}] ON [{fragment.Schema}].[{fragment.Table}] REBUILD WITH (FILLFACTOR = {ff})");
                    }
                }
                else
                {
                    //reorganize
                    commands.Add($"ALTER INDEX [{fragment.Index}] ON [{fragment.Schema}].[{fragment.Table}] REORGANIZE");
                }
            }

            Log.Information("Planning SQL Server database {DatabaseName} index defragmentation: {Commands}", db.Database, commands);
            return commands;
        }

        /// <summary>
        /// Performs whole index defragmentation to an SQL Server database. 
        /// Allows online rebuild only if the SQL Server instance is Enterprise edition.
        /// </summary>
        /// <param name="db"></param>
        /// <param name="onlineRebuild"></param>
        /// <returns></returns>
        public static async Task DefragmentAsync(this IDbConnection db, bool onlineRebuild = false)
        {
            var commands = await db.PlanDefragmentAsync(onlineRebuild);
            var sw = Stopwatch.StartNew();

            // We need to do this, because GO is not supported by ExecuteNonQuery method!
            foreach (var command in commands)
            {
                await db.ExecuteAsync(command);
            }
            Log.Information("Defragmenting SQL Server database {DatabaseName}. ({ElapsedMilliseconds} ms)", db.Database, sw.ElapsedMilliseconds);
        }
    }
}
