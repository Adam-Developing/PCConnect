package com.adamkhattab.pcconnect.v2.data

import android.content.Context
import androidx.room.Dao
import androidx.room.Database
import androidx.room.Entity
import androidx.room.Insert
import androidx.room.OnConflictStrategy
import androidx.room.Query
import androidx.room.Room
import androidx.room.RoomDatabase
import androidx.room.Transaction
import androidx.room.migration.Migration
import androidx.sqlite.db.SupportSQLiteDatabase
import kotlinx.coroutines.flow.Flow

@Entity(tableName = "devices", primaryKeys = ["id"])
data class DeviceEntity(
    val id: String,
    val displayName: String,
    val platform: String,
    val status: String,
    val lastSeenAt: String?,
    val capabilities: String,
    val version: Long,
)

@Entity(tableName = "commands", primaryKeys = ["id"])
data class CommandEntity(
    val id: String,
    val deviceId: String,
    val type: String,
    val status: String,
    val issuedAt: String,
    val failureCode: String?,
    val version: Long,
)

@Entity(tableName = "reminders", primaryKeys = ["id"])
data class ReminderEntity(
    val id: String,
    val text: String,
    val targetMode: String,
    val targetDeviceIds: String,
    val timezone: String,
    val localStart: String,
    val recurrenceRule: String?,
    val nextOccurrenceAt: String?,
    val version: Long,
    val lastAcknowledgementStatus: String?,
    val lastAcknowledgedAt: String?,
    val lastAcknowledgedBy: String?,
)

@Entity(tableName = "sync_cursors", primaryKeys = ["resource"])
data class SyncCursorEntity(val resource: String, val value: String?)

@Dao
interface ReadModelDao {
    @Query("SELECT * FROM devices ORDER BY displayName COLLATE NOCASE")
    fun observeDevices(): Flow<List<DeviceEntity>>

    @Query("SELECT * FROM commands ORDER BY issuedAt DESC LIMIT 200")
    fun observeCommands(): Flow<List<CommandEntity>>

    @Query("SELECT * FROM reminders ORDER BY COALESCE(nextOccurrenceAt, localStart)")
    fun observeReminders(): Flow<List<ReminderEntity>>

    @Query("SELECT value FROM sync_cursors WHERE resource = :resource")
    suspend fun cursor(resource: String): String?

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun upsertDevices(items: List<DeviceEntity>)

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun upsertCommands(items: List<CommandEntity>)

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun upsertReminders(items: List<ReminderEntity>)

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun upsertCursor(cursor: SyncCursorEntity)

    @Query("DELETE FROM devices")
    suspend fun clearDevices()

    @Query("DELETE FROM commands")
    suspend fun clearCommands()

    @Query("DELETE FROM reminders")
    suspend fun clearReminders()

    @Query("DELETE FROM sync_cursors")
    suspend fun clearSyncCursors()

    @Transaction
    suspend fun replaceDevices(items: List<DeviceEntity>, nextCursor: String?) {
        clearDevices()
        upsertDevices(items)
        upsertCursor(SyncCursorEntity("devices", nextCursor))
    }

    @Transaction
    suspend fun replaceCommands(items: List<CommandEntity>, nextCursor: String?) {
        clearCommands()
        upsertCommands(items)
        upsertCursor(SyncCursorEntity("commands", nextCursor))
    }

    @Transaction
    suspend fun replaceReminders(items: List<ReminderEntity>, nextCursor: String?) {
        clearReminders()
        upsertReminders(items)
        upsertCursor(SyncCursorEntity("reminders", nextCursor))
    }

    @Transaction
    suspend fun clearAll() {
        clearDevices()
        clearCommands()
        clearReminders()
        clearSyncCursors()
    }
}

@Database(
    entities = [DeviceEntity::class, CommandEntity::class, ReminderEntity::class, SyncCursorEntity::class],
    version = 3,
    exportSchema = true,
)
abstract class AppDatabase : RoomDatabase() {
    abstract fun readModel(): ReadModelDao

    companion object {
        /** Version 1 cached reminder text in plaintext; it is disposable and recovered from the API. */
        private val MIGRATION_1_2 = object : Migration(1, 2) {
            override fun migrate(db: SupportSQLiteDatabase) {
                db.execSQL("DELETE FROM reminders")
                db.execSQL("DELETE FROM sync_cursors WHERE resource = 'reminders'")
            }
        }

        private val MIGRATION_2_3 = object : Migration(2, 3) {
            override fun migrate(db: SupportSQLiteDatabase) {
                db.execSQL("ALTER TABLE reminders ADD COLUMN targetDeviceIds TEXT NOT NULL DEFAULT ''")
                db.execSQL("ALTER TABLE reminders ADD COLUMN recurrenceRule TEXT")
                db.execSQL("ALTER TABLE reminders ADD COLUMN lastAcknowledgementStatus TEXT")
                db.execSQL("ALTER TABLE reminders ADD COLUMN lastAcknowledgedAt TEXT")
                db.execSQL("ALTER TABLE reminders ADD COLUMN lastAcknowledgedBy TEXT")
            }
        }

        fun create(context: Context): AppDatabase = Room.databaseBuilder(
            context.applicationContext,
            AppDatabase::class.java,
            "pcconnect-v2-read-model.db",
        ).addMigrations(MIGRATION_1_2, MIGRATION_2_3).build()
    }
}
