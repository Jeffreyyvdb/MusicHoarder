package com.musichoarder.app.data

import android.content.Context
import android.os.Build
import android.provider.Settings
import java.util.UUID

/**
 * Who this phone is to the account's other devices.
 *
 * One device per install: [deviceId] and [installId] are the same UUID, minted once and kept for the
 * life of the install, and shared by every paired account (the server keys devices by user, so two
 * accounts on one phone cannot see each other through it). A browser tab gets a new id per tab; a
 * phone has one player, so it has one id.
 *
 * Kept in its own SharedPreferences file rather than the pairing DataStore because it is read
 * synchronously — the playback service reports from `onCreate` onwards — and excluded from backup
 * and device transfer (see `res/xml`): restored onto a new phone it would give two devices one
 * identity, and each would keep taking the session from the other.
 */
class DeviceIdentity(context: Context) {
    private val appContext = context.applicationContext

    val installId: String by lazy {
        val prefs = appContext.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
        prefs.getString(KEY_INSTALL_ID, null)?.takeIf(::isValidDeviceId)
            ?: UUID.randomUUID().toString().also { prefs.edit().putString(KEY_INSTALL_ID, it).apply() }
    }

    val deviceId: String get() = installId

    /** Read each time: the owner can rename the phone while the app runs. */
    val name: String
        get() {
            // DEVICE_NAME arrived in API 25; minSdk is 24.
            val settingsName = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.N_MR1) {
                runCatching { Settings.Global.getString(appContext.contentResolver, Settings.Global.DEVICE_NAME) }
                    .getOrNull()
            } else {
                null
            }
            return deviceNameFor(settingsName, Build.MODEL)
        }

    /** Read each time: a foldable opened out is a tablet now. */
    val kind: String
        get() {
            val configuration = appContext.resources.configuration
            return deviceKindFor(
                configuration.smallestScreenWidthDp,
                configuration.uiMode and android.content.res.Configuration.UI_MODE_TYPE_MASK,
            )
        }

    val client: String get() = PLAYBACK_CLIENT_ANDROID

    private companion object {
        const val PREFS = "playback_device"
        const val KEY_INSTALL_ID = "install_id"
    }
}

/** The wire's `^[A-Za-z0-9_-]{8,64}$` — a UUID with its dashes passes. */
fun isValidDeviceId(id: String): Boolean = DEVICE_ID_PATTERN.matches(id)

private val DEVICE_ID_PATTERN = Regex("^[A-Za-z0-9_-]{8,64}$")
