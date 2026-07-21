using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;

namespace CodexLocalRetrieval.Core.Memory;

// The DERIVED SQLite index over a brain. It is NEVER the source of truth: it is rebuilt by scanning
// the canonical Markdown vault (cards) plus the deterministic source blocks (from chats). Delete the
// file and rebuild — nothing important is lost. That disposability is a sacred invariant.
public sealed class BrainIndex
{
    private readonly string _dbPath;
    private string Conn => $"Data Source={_dbPath}";

    public BrainIndex(string dbPath)
    {
        _dbPath = dbPath;
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
    }

    public void Delete()
    {
        // SqliteConnection pools the underlying handle, which keeps the file open and blocks deletion.
        // Releasing the pool first is what makes the index genuinely disposable on disk.
        try { SqliteConnection.ClearAllPools(); } catch { }
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }

    // Full rebuild from canonical inputs: cards parsed from the vault, blocks from the chats, and the
    // manifest (incorporated chats). Drops and recreates every table — deterministic, idempotent.
    public void Rebuild(IReadOnlyList<MemoryCard> cards, IReadOnlyList<SourceBlock> blocks, BrainManifest manifest)
    {
        using var c = new SqliteConnection(Conn);
        c.Open();
        Exec(c,
            @"DROP TABLE IF EXISTS sources;
              DROP TABLE IF EXISTS blocks;
              DROP TABLE IF EXISTS cards;
              DROP TABLE IF EXISTS card_sources;
              DROP TABLE IF EXISTS card_fts;
              CREATE TABLE sources(session_id TEXT PRIMARY KEY, source_path TEXT, tool TEXT, file_stamp TEXT);
              CREATE TABLE blocks(id TEXT PRIMARY KEY, session_id TEXT, idx INTEGER, msg_start INTEGER,
                  msg_end INTEGER, ts_start TEXT, ts_end TEXT, msg_count INTEGER, approx_tokens INTEGER,
                  source_hash TEXT, excerpt TEXT);
              CREATE TABLE cards(id TEXT PRIMARY KEY, title TEXT, lane TEXT, type TEXT, status TEXT,
                  truth_evidence TEXT, extraction_confidence TEXT, importance INTEGER, durability INTEGER,
                  actionability INTEGER, created_by TEXT, created_at TEXT, source_coverage TEXT,
                  sensitivity TEXT, has_anchor INTEGER, body TEXT);
              CREATE TABLE card_sources(card_id TEXT, session_id TEXT, block_id TEXT, msg_start INTEGER,
                  msg_end INTEGER, quote_hash TEXT);
              CREATE VIRTUAL TABLE card_fts USING fts5(id UNINDEXED, title, body);
              CREATE INDEX ix_blocks_session ON blocks(session_id);
              CREATE INDEX ix_cardsrc_card ON card_sources(card_id);");

        using var tx = c.BeginTransaction();

        foreach (var kv in manifest.IncorporatedChats)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT OR REPLACE INTO sources(session_id, source_path, tool, file_stamp) VALUES(@s,@p,@t,@f)";
            cmd.Parameters.AddWithValue("@s", kv.Key);
            cmd.Parameters.AddWithValue("@p", "");
            cmd.Parameters.AddWithValue("@t", "");
            cmd.Parameters.AddWithValue("@f", kv.Value ?? "");
            cmd.ExecuteNonQuery();
        }

        foreach (var b in blocks)
        {
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "INSERT OR REPLACE INTO sources(session_id, source_path, tool, file_stamp) VALUES(@s,@p,@t,@f)";
                cmd.Parameters.AddWithValue("@s", b.SessionId);
                cmd.Parameters.AddWithValue("@p", b.SourcePath ?? "");
                cmd.Parameters.AddWithValue("@t", b.Tool ?? "");
                cmd.Parameters.AddWithValue("@f", b.FileStamp ?? "");
                cmd.ExecuteNonQuery();
            }
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = @"INSERT OR REPLACE INTO blocks(id, session_id, idx, msg_start, msg_end, ts_start, ts_end,
                    msg_count, approx_tokens, source_hash, excerpt) VALUES(@id,@s,@i,@ms,@me,@ts,@te,@mc,@at,@sh,@ex)";
                cmd.Parameters.AddWithValue("@id", b.Id);
                cmd.Parameters.AddWithValue("@s", b.SessionId);
                cmd.Parameters.AddWithValue("@i", b.Index);
                cmd.Parameters.AddWithValue("@ms", b.MsgStartIndex);
                cmd.Parameters.AddWithValue("@me", b.MsgEndIndex);
                cmd.Parameters.AddWithValue("@ts", b.TsStart ?? "");
                cmd.Parameters.AddWithValue("@te", b.TsEnd ?? "");
                cmd.Parameters.AddWithValue("@mc", b.MessageCount);
                cmd.Parameters.AddWithValue("@at", b.ApproxTokens);
                cmd.Parameters.AddWithValue("@sh", b.SourceHash ?? "");
                cmd.Parameters.AddWithValue("@ex", b.Excerpt ?? "");
                cmd.ExecuteNonQuery();
            }
        }

        foreach (var card in cards)
        {
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = @"INSERT OR REPLACE INTO cards(id,title,lane,type,status,truth_evidence,
                    extraction_confidence,importance,durability,actionability,created_by,created_at,
                    source_coverage,sensitivity,has_anchor,body)
                    VALUES(@id,@ti,@la,@ty,@st,@tr,@ec,@im,@du,@ac,@cb,@ca,@sc,@se,@ha,@bo)";
                cmd.Parameters.AddWithValue("@id", card.Id);
                cmd.Parameters.AddWithValue("@ti", card.Title ?? "");
                cmd.Parameters.AddWithValue("@la", card.Lane ?? "");
                cmd.Parameters.AddWithValue("@ty", card.Type ?? "");
                cmd.Parameters.AddWithValue("@st", card.Status ?? "");
                cmd.Parameters.AddWithValue("@tr", card.TruthEvidence ?? "");
                cmd.Parameters.AddWithValue("@ec", card.ExtractionConfidence ?? "");
                cmd.Parameters.AddWithValue("@im", card.Importance);
                cmd.Parameters.AddWithValue("@du", card.Durability);
                cmd.Parameters.AddWithValue("@ac", card.Actionability);
                cmd.Parameters.AddWithValue("@cb", card.CreatedBy ?? "");
                cmd.Parameters.AddWithValue("@ca", card.CreatedAt ?? "");
                cmd.Parameters.AddWithValue("@sc", card.SourceCoverage ?? "");
                cmd.Parameters.AddWithValue("@se", card.Sensitivity ?? "");
                cmd.Parameters.AddWithValue("@ha", card.HasSourceAnchor ? 1 : 0);
                cmd.Parameters.AddWithValue("@bo", card.Body ?? "");
                cmd.ExecuteNonQuery();
            }
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "INSERT INTO card_fts(id,title,body) VALUES(@id,@ti,@bo)";
                cmd.Parameters.AddWithValue("@id", card.Id);
                cmd.Parameters.AddWithValue("@ti", card.Title ?? "");
                cmd.Parameters.AddWithValue("@bo", card.Body ?? "");
                cmd.ExecuteNonQuery();
            }
            foreach (var s in card.Sources)
            {
                using var cmd = c.CreateCommand();
                cmd.CommandText = @"INSERT INTO card_sources(card_id,session_id,block_id,msg_start,msg_end,quote_hash)
                    VALUES(@c,@s,@b,@ms,@me,@q)";
                cmd.Parameters.AddWithValue("@c", card.Id);
                cmd.Parameters.AddWithValue("@s", s.SessionId ?? "");
                cmd.Parameters.AddWithValue("@b", s.BlockId ?? "");
                cmd.Parameters.AddWithValue("@ms", s.MsgStartIndex);
                cmd.Parameters.AddWithValue("@me", s.MsgEndIndex);
                cmd.Parameters.AddWithValue("@q", s.QuoteHash ?? "");
                cmd.ExecuteNonQuery();
            }
        }

        tx.Commit();
    }

    public int CardCount() => ScalarInt("SELECT COUNT(*) FROM cards");
    public int BlockCount() => ScalarInt("SELECT COUNT(*) FROM blocks");
    public int SourceCount() => ScalarInt("SELECT COUNT(*) FROM sources");

    public string? GetCardTitle(string id)
    {
        using var c = new SqliteConnection(Conn);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT title FROM cards WHERE id=@id";
        cmd.Parameters.AddWithValue("@id", id);
        return cmd.ExecuteScalar() as string;
    }

    public string? GetCardLane(string id)
    {
        using var c = new SqliteConnection(Conn);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT lane FROM cards WHERE id=@id";
        cmd.Parameters.AddWithValue("@id", id);
        return cmd.ExecuteScalar() as string;
    }

    // Full-text search over card titles + bodies. Returns matching card ids (FTS5).
    public List<string> Search(string query)
    {
        var ids = new List<string>();
        using var c = new SqliteConnection(Conn);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id FROM card_fts WHERE card_fts MATCH @q ORDER BY rank";
        cmd.Parameters.AddWithValue("@q", query);
        using var r = cmd.ExecuteReader();
        while (r.Read()) ids.Add(r.GetString(0));
        return ids;
    }

    // The anchor coordinates for a card — the "trace to source" the UI uses.
    public List<SourceAnchor> CardSources(string cardId)
    {
        var list = new List<SourceAnchor>();
        using var c = new SqliteConnection(Conn);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT session_id,block_id,msg_start,msg_end,quote_hash FROM card_sources WHERE card_id=@c";
        cmd.Parameters.AddWithValue("@c", cardId);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new SourceAnchor
            {
                SessionId = r.GetString(0),
                BlockId = r.GetString(1),
                MsgStartIndex = r.GetInt32(2),
                MsgEndIndex = r.GetInt32(3),
                QuoteHash = r.GetString(4),
            });
        return list;
    }

    private int ScalarInt(string sql)
    {
        using var c = new SqliteConnection(Conn);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    private static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
