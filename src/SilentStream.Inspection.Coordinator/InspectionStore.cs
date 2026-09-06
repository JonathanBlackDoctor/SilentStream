using System.Text.Json;
using Microsoft.Data.Sqlite;
using SilentStream.Inspection;

namespace SilentStream.Inspection.Coordinator;

public sealed class InspectionStore
{
    private readonly string _connectionString;
    private readonly object _eventGate = new();
    private readonly object _auditGate = new();
    private readonly object _runCreationGate = new();
    private bool _maintenanceReserved;
    private readonly string _room;

    public InspectionStore(CoordinatorOptionsStore options)
    {
        _room = options.Current.Room;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = options.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
        Initialize();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    private void Initialize()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS bundles (
              bundle_id TEXT PRIMARY KEY, version TEXT NOT NULL, manifest_json TEXT NOT NULL,
              extracted_path TEXT NOT NULL, uploaded_at_utc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS runs (
              run_id TEXT PRIMARY KEY, bundle_id TEXT NOT NULL, status TEXT NOT NULL,
              created_at_utc TEXT NOT NULL, updated_at_utc TEXT NOT NULL,
              owner TEXT NOT NULL, destructive_approved INTEGER NOT NULL DEFAULT 0,
              pause_requested INTEGER NOT NULL DEFAULT 0, cancel_requested INTEGER NOT NULL DEFAULT 0,
              message TEXT NOT NULL DEFAULT '', FOREIGN KEY(bundle_id) REFERENCES bundles(bundle_id)
            );
            CREATE TABLE IF NOT EXISTS steps (
              run_id TEXT NOT NULL, step_id TEXT NOT NULL, type TEXT NOT NULL, required INTEGER NOT NULL,
              status TEXT NOT NULL, attempt INTEGER NOT NULL DEFAULT 0, started_at_utc TEXT,
              completed_at_utc TEXT, message TEXT NOT NULL DEFAULT '', output_json TEXT,
              PRIMARY KEY(run_id, step_id), FOREIGN KEY(run_id) REFERENCES runs(run_id)
            );
            CREATE TABLE IF NOT EXISTS events (
              sequence INTEGER PRIMARY KEY AUTOINCREMENT, run_id TEXT, timestamp_utc TEXT NOT NULL,
              kind TEXT NOT NULL, payload_json TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_events_run_sequence ON events(run_id, sequence);
            CREATE TABLE IF NOT EXISTS artifacts (
              artifact_id TEXT PRIMARY KEY, run_id TEXT NOT NULL, step_id TEXT NOT NULL,
              relative_path TEXT NOT NULL, sha256 TEXT NOT NULL, length INTEGER NOT NULL,
              created_at_utc TEXT NOT NULL, FOREIGN KEY(run_id) REFERENCES runs(run_id)
            );
            CREATE TABLE IF NOT EXISTS audit (
              sequence INTEGER PRIMARY KEY AUTOINCREMENT, timestamp_utc TEXT NOT NULL,
              room TEXT NOT NULL, token_id TEXT NOT NULL, action TEXT NOT NULL, result TEXT NOT NULL, details TEXT NOT NULL,
              previous_hash TEXT NOT NULL, entry_hash TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS control_leases (
              lease_id TEXT PRIMARY KEY, owner TEXT NOT NULL, acquired_at_utc TEXT NOT NULL,
              updated_at_utc TEXT NOT NULL, expires_at_utc TEXT NOT NULL, status TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS external_acks (
              run_id TEXT NOT NULL, channel TEXT NOT NULL, token_id TEXT NOT NULL, received_at_utc TEXT NOT NULL,
              PRIMARY KEY(run_id,channel), FOREIGN KEY(run_id) REFERENCES runs(run_id)
            );
            CREATE TABLE IF NOT EXISTS startup_runs (
              boot_id TEXT PRIMARY KEY, run_id TEXT NOT NULL UNIQUE, created_at_utc TEXT NOT NULL,
              FOREIGN KEY(run_id) REFERENCES runs(run_id)
            );
            """;
        command.ExecuteNonQuery();
    }

    public void SaveBundle(VerifiedInspectionBundle bundle)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO bundles(bundle_id,version,manifest_json,extracted_path,uploaded_at_utc)
            VALUES($id,$version,$manifest,$path,$now)
            ON CONFLICT(bundle_id) DO UPDATE SET version=$version,manifest_json=$manifest,
              extracted_path=$path,uploaded_at_utc=$now;
            """;
        command.Parameters.AddWithValue("$id", bundle.Manifest.BundleId);
        command.Parameters.AddWithValue("$version", bundle.Manifest.Version);
        command.Parameters.AddWithValue("$manifest", JsonSerializer.Serialize(bundle.Manifest, InspectionProtocol.Json));
        command.Parameters.AddWithValue("$path", bundle.ExtractedPath);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
        AppendEvent(null, "bundle.uploaded", new { bundle.Manifest.BundleId, bundle.Manifest.Version });
    }

    public StoredBundle? GetBundle(string bundleId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT manifest_json,extracted_path FROM bundles WHERE bundle_id=$id";
        command.Parameters.AddWithValue("$id", bundleId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var manifest = JsonSerializer.Deserialize<InspectionBundleManifest>(reader.GetString(0), InspectionProtocol.Json)!;
        return new StoredBundle(manifest, reader.GetString(1));
    }

    public IReadOnlyList<object> ListBundles()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT bundle_id,version,uploaded_at_utc FROM bundles ORDER BY uploaded_at_utc DESC";
        using var reader = command.ExecuteReader();
        var values = new List<object>();
        while (reader.Read()) values.Add(new { bundleId = reader.GetString(0), version = reader.GetString(1), uploadedAtUtc = reader.GetString(2) });
        return values;
    }

    public IReadOnlyList<StoredBundle> ListStoredBundles()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT manifest_json,extracted_path FROM bundles ORDER BY uploaded_at_utc DESC";
        using var reader = command.ExecuteReader();
        var values = new List<StoredBundle>();
        while (reader.Read())
        {
            var manifest = JsonSerializer.Deserialize<InspectionBundleManifest>(reader.GetString(0), InspectionProtocol.Json);
            if (manifest is not null) values.Add(new StoredBundle(manifest, reader.GetString(1)));
        }
        return values;
    }

    public string CreateRun(StoredBundle bundle, string owner, bool destructiveApproved)
    {
        lock (_runCreationGate) return CreateRunCore(bundle, owner, destructiveApproved);
    }

    public bool TryCreateRunWhenIdle(StoredBundle bundle, string owner, bool destructiveApproved, out string runId)
    {
        lock (_runCreationGate)
        {
            if (_maintenanceReserved)
            {
                runId = string.Empty;
                return false;
            }
            var active = GetExecutableRuns().FirstOrDefault();
            if (active is not null)
            {
                runId = active;
                return false;
            }
            runId = CreateRunCore(bundle, owner, destructiveApproved);
            return true;
        }
    }

    /// <summary>
    /// Atomically reserves the no-run window used to hand a signed service update to the broker.
    /// New operator runs are rejected until the reservation is released or this process is
    /// replaced by the updater, preventing an update from cutting through a newly queued run.
    /// </summary>
    public bool TryBeginMaintenance(out string? blockingRunId)
    {
        lock (_runCreationGate)
        {
            blockingRunId = GetExecutableRuns().FirstOrDefault();
            if (_maintenanceReserved || blockingRunId is not null) return false;
            _maintenanceReserved = true;
            return true;
        }
    }

    public void EndMaintenance()
    {
        lock (_runCreationGate) _maintenanceReserved = false;
    }

    private string CreateRunCore(StoredBundle bundle, string owner, bool destructiveApproved)
    {
        var id = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow.ToString("O");
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO runs(run_id,bundle_id,status,created_at_utc,updated_at_utc,owner,destructive_approved) VALUES($id,$bundle,'Queued',$now,$now,$owner,$approved)";
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$bundle", bundle.Manifest.BundleId);
            command.Parameters.AddWithValue("$now", now);
            command.Parameters.AddWithValue("$owner", owner);
            command.Parameters.AddWithValue("$approved", destructiveApproved ? 1 : 0);
            command.ExecuteNonQuery();
        }
        foreach (var step in bundle.Manifest.Plan.Steps)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO steps(run_id,step_id,type,required,status) VALUES($run,$step,$type,$required,'Queued')";
            command.Parameters.AddWithValue("$run", id);
            command.Parameters.AddWithValue("$step", step.Id);
            command.Parameters.AddWithValue("$type", step.Type.ToString());
            command.Parameters.AddWithValue("$required", step.Required ? 1 : 0);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
        AppendEvent(id, "run.queued", new { runId = id, bundleId = bundle.Manifest.BundleId });
        return id;
    }

    public bool TryCreateStartupRun(StoredBundle bundle, string bootId, out string runId)
    {
        lock (_runCreationGate)
        {
            if (_maintenanceReserved)
            {
                runId = string.Empty;
                return false;
            }
            return TryCreateStartupRunCore(bundle, bootId, out runId);
        }
    }

    private bool TryCreateStartupRunCore(StoredBundle bundle, string bootId, out string runId)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using (var existing = connection.CreateCommand())
        {
            existing.Transaction = transaction;
            existing.CommandText = "SELECT run_id FROM startup_runs WHERE boot_id=$boot";
            existing.Parameters.AddWithValue("$boot", bootId);
            if (existing.ExecuteScalar() is string value)
            {
                runId = value;
                transaction.Commit();
                return false;
            }
        }

        runId = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow.ToString("O");
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO runs(run_id,bundle_id,status,created_at_utc,updated_at_utc,owner,destructive_approved) VALUES($id,$bundle,'Queued',$now,$now,$owner,0)";
            command.Parameters.AddWithValue("$id", runId);
            command.Parameters.AddWithValue("$bundle", bundle.Manifest.BundleId);
            command.Parameters.AddWithValue("$now", now);
            command.Parameters.AddWithValue("$owner", $"system:startup:{bootId}");
            command.ExecuteNonQuery();
        }
        foreach (var step in bundle.Manifest.Plan.Steps)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO steps(run_id,step_id,type,required,status) VALUES($run,$step,$type,$required,'Queued')";
            command.Parameters.AddWithValue("$run", runId);
            command.Parameters.AddWithValue("$step", step.Id);
            command.Parameters.AddWithValue("$type", step.Type.ToString());
            command.Parameters.AddWithValue("$required", step.Required ? 1 : 0);
            command.ExecuteNonQuery();
        }
        using (var marker = connection.CreateCommand())
        {
            marker.Transaction = transaction;
            marker.CommandText = "INSERT INTO startup_runs(boot_id,run_id,created_at_utc) VALUES($boot,$run,$now)";
            marker.Parameters.AddWithValue("$boot", bootId);
            marker.Parameters.AddWithValue("$run", runId);
            marker.Parameters.AddWithValue("$now", now);
            marker.ExecuteNonQuery();
        }
        transaction.Commit();
        AppendEvent(runId, "run.queued", new { runId, bundleId = bundle.Manifest.BundleId, bootId, automatic = true });
        return true;
    }

    public string? GetLatestRunId()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT run_id FROM runs ORDER BY created_at_utc DESC LIMIT 1";
        return command.ExecuteScalar() as string;
    }

    public IReadOnlyList<object> ListRuns(int limit = 50)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT run_id,bundle_id,status,created_at_utc,updated_at_utc,message,owner FROM runs ORDER BY created_at_utc DESC LIMIT $limit";
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 200));
        using var reader = command.ExecuteReader();
        var runs = new List<object>();
        while (reader.Read())
        {
            runs.Add(new
            {
                runId = reader.GetString(0), bundleId = reader.GetString(1), status = reader.GetString(2).ToLowerInvariant(),
                createdAtUtc = reader.GetString(3), updatedAtUtc = reader.GetString(4), message = reader.GetString(5),
                automatic = reader.GetString(6).StartsWith("system:startup:", StringComparison.Ordinal)
            });
        }
        return runs;
    }

    public InspectionRunProgress? GetRunProgress(string runId)
    {
        var run = GetStoredRun(runId);
        if (run is null) return null;
        var bundle = GetBundle(run.BundleId);
        if (bundle is null) return null;
        var states = GetSteps(runId);
        var ordered = bundle.Manifest.Plan.Steps
            .Where(step => states.ContainsKey(step.Id))
            .Select(step => states[step.Id])
            .ToArray();
        var terminal = ordered.Count(step => IsTerminal(step.Status));
        var current = ordered.FirstOrDefault(step => step.Status == InspectionRunStatus.Running)
            ?? ordered.FirstOrDefault(step => !IsTerminal(step.Status));
        var issues = ordered
            .Where(step => step.Status is InspectionRunStatus.Warning or InspectionRunStatus.Fail or InspectionRunStatus.Unknown)
            .Select(step => $"{step.StepId}: {step.Message}")
            .ToArray();
        return new InspectionRunProgress(run.RunId, run.BundleId, run.Status, ordered.Length, terminal,
            current?.StepId, current?.Message, issues);
    }

    public IReadOnlyList<string> GetExecutableRuns()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT run_id FROM runs WHERE status IN ('Queued','Running','Paused') AND cancel_requested=0 ORDER BY created_at_utc";
        using var reader = command.ExecuteReader();
        var ids = new List<string>();
        while (reader.Read()) ids.Add(reader.GetString(0));
        return ids;
    }

    public StoredRun? GetStoredRun(string runId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT bundle_id,status,owner,destructive_approved,pause_requested,cancel_requested FROM runs WHERE run_id=$id";
        command.Parameters.AddWithValue("$id", runId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return new StoredRun(runId, reader.GetString(0), ParseStatus(reader.GetString(1)), reader.GetString(2), reader.GetInt32(3) != 0, reader.GetInt32(4) != 0, reader.GetInt32(5) != 0);
    }

    public IReadOnlyDictionary<string, StoredStep> GetSteps(string runId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT step_id,status,attempt,message FROM steps WHERE run_id=$run";
        command.Parameters.AddWithValue("$run", runId);
        using var reader = command.ExecuteReader();
        var values = new Dictionary<string, StoredStep>(StringComparer.Ordinal);
        while (reader.Read()) values[reader.GetString(0)] = new StoredStep(reader.GetString(0), ParseStatus(reader.GetString(1)), reader.GetInt32(2), reader.GetString(3));
        return values;
    }

    public JsonElement? GetStepOutput(string runId, string stepId)
    {
        using var connection=Open();using var command=connection.CreateCommand();command.CommandText="SELECT output_json FROM steps WHERE run_id=$run AND step_id=$step";command.Parameters.AddWithValue("$run",runId);command.Parameters.AddWithValue("$step",stepId);
        var raw=command.ExecuteScalar() as string;return string.IsNullOrWhiteSpace(raw)?null:JsonSerializer.Deserialize<JsonElement>(raw,InspectionProtocol.Json);
    }

    public void UpdateRun(string runId, InspectionRunStatus status, string message = "")
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE runs SET status=$status,message=$message,updated_at_utc=$now WHERE run_id=$id";
        command.Parameters.AddWithValue("$status", status.ToString());
        command.Parameters.AddWithValue("$message", message);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id", runId);
        command.ExecuteNonQuery();
        AppendEvent(runId, "run.status", new { status = status.ToString().ToLowerInvariant(), message });
    }

    public void SetRunRequest(string runId, bool? pause = null, bool? cancel = null)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        var assignments = new List<string>();
        if (pause.HasValue) { assignments.Add("pause_requested=$pause"); command.Parameters.AddWithValue("$pause", pause.Value ? 1 : 0); }
        if (cancel.HasValue) { assignments.Add("cancel_requested=$cancel"); command.Parameters.AddWithValue("$cancel", cancel.Value ? 1 : 0); }
        if (assignments.Count == 0) return;
        assignments.Add("updated_at_utc=$now");
        command.CommandText = $"UPDATE runs SET {string.Join(',', assignments)} WHERE run_id=$id";
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id", runId);
        command.ExecuteNonQuery();
    }

    public void UpdateStep(string runId, string stepId, InspectionRunStatus status, int attempt, string message, object? output = null)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE steps SET status=$status,attempt=$attempt,message=$message,output_json=$output,
              started_at_utc=CASE WHEN $status='Running' AND started_at_utc IS NULL THEN $now ELSE started_at_utc END,
              completed_at_utc=CASE WHEN $terminal=1 THEN $now ELSE completed_at_utc END
            WHERE run_id=$run AND step_id=$step
            """;
        command.Parameters.AddWithValue("$status", status.ToString());
        command.Parameters.AddWithValue("$attempt", attempt);
        command.Parameters.AddWithValue("$message", message);
        command.Parameters.AddWithValue("$output", output is null ? DBNull.Value : JsonSerializer.Serialize(output, InspectionProtocol.Json));
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$terminal", IsTerminal(status) ? 1 : 0);
        command.Parameters.AddWithValue("$run", runId);
        command.Parameters.AddWithValue("$step", stepId);
        command.ExecuteNonQuery();
        AppendEvent(runId, "step.status", new { stepId, status = status.ToString().ToLowerInvariant(), attempt, message });
    }

    public long AppendEvent(string? runId, string kind, object payload)
    {
        lock (_eventGate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO events(run_id,timestamp_utc,kind,payload_json) VALUES($run,$now,$kind,$payload); SELECT last_insert_rowid();";
            command.Parameters.AddWithValue("$run", runId is null ? DBNull.Value : runId);
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$kind", kind);
            command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(payload, InspectionProtocol.Json));
            return (long)(command.ExecuteScalar() ?? 0L);
        }
    }

    public IReadOnlyList<object> GetEvents(string? runId, long after, int limit = 500)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = runId is null
            ? "SELECT sequence,run_id,timestamp_utc,kind,payload_json FROM events WHERE sequence>$after ORDER BY sequence LIMIT $limit"
            : "SELECT sequence,run_id,timestamp_utc,kind,payload_json FROM events WHERE sequence>$after AND run_id=$run ORDER BY sequence LIMIT $limit";
        command.Parameters.AddWithValue("$after", after);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 10000));
        if (runId is not null) command.Parameters.AddWithValue("$run", runId);
        using var reader = command.ExecuteReader();
        var values = new List<object>();
        while (reader.Read()) values.Add(new
        {
            sequence = reader.GetInt64(0),
            runId = reader.IsDBNull(1) ? null : reader.GetString(1),
            timestampUtc = reader.GetString(2),
            kind = reader.GetString(3),
            payload = JsonSerializer.Deserialize<JsonElement>(reader.GetString(4), InspectionProtocol.Json)
        });
        return values;
    }

    public object? GetRunSnapshot(string runId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT bundle_id,status,created_at_utc,updated_at_utc,message FROM runs WHERE run_id=$id";
        command.Parameters.AddWithValue("$id", runId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var bundleId = reader.GetString(0); var status = reader.GetString(1); var created = reader.GetString(2); var updated = reader.GetString(3); var message = reader.GetString(4);
        reader.Close();
        var approvalSteps = (GetBundle(bundleId)?.Manifest.Plan.Steps ?? [])
            .Where(step => step.Input.ValueKind == JsonValueKind.Object && step.Input.TryGetProperty("action", out var action)
                && action.GetString() is "uninstall-app" or "nic-bounce" or "reboot")
            .Select(step => step.Id).ToHashSet(StringComparer.Ordinal);
        using var stepsCommand = connection.CreateCommand();
        stepsCommand.CommandText = "SELECT step_id,type,required,status,attempt,started_at_utc,completed_at_utc,message,output_json FROM steps WHERE run_id=$id ORDER BY rowid";
        stepsCommand.Parameters.AddWithValue("$id", runId);
        using var stepsReader = stepsCommand.ExecuteReader();
        var steps = new List<object>();
        while (stepsReader.Read()) steps.Add(new
        {
            stepId = stepsReader.GetString(0), type = stepsReader.GetString(1), required = stepsReader.GetInt32(2) != 0,
            status = stepsReader.GetString(3).ToLowerInvariant(), attempt = stepsReader.GetInt32(4),
            startedAtUtc = stepsReader.IsDBNull(5) ? null : stepsReader.GetString(5), completedAtUtc = stepsReader.IsDBNull(6) ? null : stepsReader.GetString(6),
            message = stepsReader.GetString(7), approvalRequired = approvalSteps.Contains(stepsReader.GetString(0)),
            output = stepsReader.IsDBNull(8) ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(stepsReader.GetString(8), InspectionProtocol.Json)
        });
        return new { runId, bundleId, status = status.ToLowerInvariant(), createdAtUtc = created, updatedAtUtc = updated, message, steps };
    }

    public void Audit(string tokenId, string action, string result, string details = "")
    {
        lock (_auditGate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            string previous;
            using (var previousCommand = connection.CreateCommand())
            {
                previousCommand.Transaction = transaction; previousCommand.CommandText = "SELECT entry_hash FROM audit ORDER BY sequence DESC LIMIT 1";
                previous = previousCommand.ExecuteScalar() as string ?? new string('0', 64);
            }
            var now = DateTimeOffset.UtcNow.ToString("O");
            var canonical = $"{previous}\n{now}\n{_room}\n{tokenId}\n{action}\n{result}\n{details}";
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO audit(timestamp_utc,room,token_id,action,result,details,previous_hash,entry_hash) VALUES($now,$room,$token,$action,$result,$details,$previous,$hash)";
            command.Parameters.AddWithValue("$now", now); command.Parameters.AddWithValue("$room", _room); command.Parameters.AddWithValue("$token", tokenId);
            command.Parameters.AddWithValue("$action", action); command.Parameters.AddWithValue("$result", result); command.Parameters.AddWithValue("$details", details);
            command.Parameters.AddWithValue("$previous", previous); command.Parameters.AddWithValue("$hash", hash); command.ExecuteNonQuery(); transaction.Commit();
        }
    }

    public bool VerifyAuditChain()
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT timestamp_utc,room,token_id,action,result,details,previous_hash,entry_hash FROM audit ORDER BY sequence";
        using var reader = command.ExecuteReader(); var expectedPrevious = new string('0', 64);
        while (reader.Read())
        {
            var previous = reader.GetString(6); if (previous != expectedPrevious) return false;
            var canonical = $"{previous}\n{reader.GetString(0)}\n{reader.GetString(1)}\n{reader.GetString(2)}\n{reader.GetString(3)}\n{reader.GetString(4)}\n{reader.GetString(5)}";
            var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
            if (reader.GetString(7) != expected) return false; expectedPrevious = expected;
        }
        return true;
    }

    public void SaveControlLease(string leaseId, string owner, DateTimeOffset now, DateTimeOffset expires, string status)
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO control_leases(lease_id,owner,acquired_at_utc,updated_at_utc,expires_at_utc,status) VALUES($id,$owner,$now,$now,$expires,$status) ON CONFLICT(lease_id) DO UPDATE SET updated_at_utc=$now,expires_at_utc=$expires,status=$status";
        command.Parameters.AddWithValue("$id", leaseId); command.Parameters.AddWithValue("$owner", owner); command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.Parameters.AddWithValue("$expires", expires.ToString("O")); command.Parameters.AddWithValue("$status", status); command.ExecuteNonQuery();
    }

    public IReadOnlyList<object> ListArtifacts(string runId)
    {
        using var connection = Open(); using var command = connection.CreateCommand(); command.CommandText = "SELECT artifact_id,step_id,relative_path,sha256,length,created_at_utc FROM artifacts WHERE run_id=$run ORDER BY created_at_utc"; command.Parameters.AddWithValue("$run", runId);
        using var reader = command.ExecuteReader(); var values = new List<object>();
        while (reader.Read()) values.Add(new { artifactId=reader.GetString(0),stepId=reader.GetString(1),fileName=Path.GetFileName(reader.GetString(2)),sha256=reader.GetString(3),length=reader.GetInt64(4),createdAtUtc=reader.GetString(5) }); return values;
    }

    public void SaveAck(string runId, string channel, string tokenId)
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO external_acks(run_id,channel,token_id,received_at_utc) VALUES($run,$channel,$token,$now) ON CONFLICT(run_id,channel) DO UPDATE SET token_id=$token,received_at_utc=$now";
        command.Parameters.AddWithValue("$run",runId);command.Parameters.AddWithValue("$channel",channel);command.Parameters.AddWithValue("$token",tokenId);command.Parameters.AddWithValue("$now",DateTimeOffset.UtcNow.ToString("O"));command.ExecuteNonQuery();
        AppendEvent(runId,"external.ack",new{channel,tokenId});
    }

    public bool HasAck(string runId, string channel)
    {
        using var connection=Open();using var command=connection.CreateCommand();command.CommandText="SELECT 1 FROM external_acks WHERE run_id=$run AND channel=$channel LIMIT 1";command.Parameters.AddWithValue("$run",runId);command.Parameters.AddWithValue("$channel",channel);return command.ExecuteScalar() is not null;
    }

    public void AddArtifact(string artifactId, string runId, string stepId, string relativePath, string sha256, long length)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO artifacts(artifact_id,run_id,step_id,relative_path,sha256,length,created_at_utc) VALUES($id,$run,$step,$path,$hash,$length,$now)";
        command.Parameters.AddWithValue("$id", artifactId); command.Parameters.AddWithValue("$run", runId); command.Parameters.AddWithValue("$step", stepId);
        command.Parameters.AddWithValue("$path", relativePath); command.Parameters.AddWithValue("$hash", sha256); command.Parameters.AddWithValue("$length", length);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O")); command.ExecuteNonQuery();
    }

    public (string RelativePath, string Sha256)? GetArtifact(string runId, string artifactId)
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT relative_path,sha256 FROM artifacts WHERE run_id=$run AND artifact_id=$id";
        command.Parameters.AddWithValue("$run", runId); command.Parameters.AddWithValue("$id", artifactId);
        using var reader = command.ExecuteReader(); return reader.Read() ? (reader.GetString(0), reader.GetString(1)) : null;
    }

    public static bool IsTerminal(InspectionRunStatus status) => status is InspectionRunStatus.Pass or InspectionRunStatus.Warning
        or InspectionRunStatus.Fail or InspectionRunStatus.Unknown or InspectionRunStatus.Skipped or InspectionRunStatus.Cancelled;
    private static InspectionRunStatus ParseStatus(string value) => Enum.Parse<InspectionRunStatus>(value, true);
}

public sealed record StoredBundle(InspectionBundleManifest Manifest, string Path);
public sealed record StoredRun(string RunId, string BundleId, InspectionRunStatus Status, string Owner, bool DestructiveApproved, bool PauseRequested, bool CancelRequested);
public sealed record StoredStep(string StepId, InspectionRunStatus Status, int Attempt, string Message);
public sealed record InspectionRunProgress(string RunId, string BundleId, InspectionRunStatus Status,
    int TotalSteps, int TerminalSteps, string? CurrentStep, string? CurrentMessage, string[] Issues);
