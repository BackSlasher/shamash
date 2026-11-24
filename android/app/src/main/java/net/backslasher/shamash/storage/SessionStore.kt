package net.backslasher.shamash.storage

import java.io.File
import java.time.Instant
import java.time.ZoneId
import java.time.format.DateTimeFormatter

class SessionFiles(val id: String, val wav: File, val json: File)

interface SessionStore {
    /** Reserves `<id>.wav` / `<id>.json` names for a session starting at [wallMs]. */
    fun create(wallMs: Long): SessionFiles

    fun save(files: SessionFiles, record: SessionRecord)
}

/** Never deletes anything. */
class FileSessionStore(
    private val dir: File,
    private val zone: ZoneId = ZoneId.systemDefault(),
) : SessionStore {
    private val nameFormat = DateTimeFormatter.ofPattern("yyyyMMdd-HHmmss").withZone(zone)

    override fun create(wallMs: Long): SessionFiles {
        dir.mkdirs()
        val base = nameFormat.format(Instant.ofEpochMilli(wallMs))
        var id = base
        var n = 1
        while (File(dir, "$id.wav").exists() || File(dir, "$id.json").exists()) id = "$base-${n++}"
        return SessionFiles(id, File(dir, "$id.wav"), File(dir, "$id.json"))
    }

    override fun save(files: SessionFiles, record: SessionRecord) {
        files.json.writeText(record.toJson(), Charsets.UTF_8)
    }
}
