package com.musichoarder.app.ui

import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.musichoarder.app.data.DeviceKind
import com.musichoarder.app.data.DevicePicker
import com.musichoarder.app.data.DevicePickerRow
import com.musichoarder.app.data.PlaybackMode
import com.musichoarder.app.player.PlayerUiState
import com.musichoarder.app.ui.theme.MusicHoarderTheme
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith

/**
 * The mini player's device picker, opened from its "Playing on MacBook" line, closes for good when
 * its control goes away under it (the feature turned off: a share started playing here, say) — it
 * must not slide back up by itself the next time the control returns.
 *
 * Test names stay camelCase here: a backticked one needs DEX 040, and this module's minSdk is 24.
 */
@RunWith(AndroidJUnit4::class)
class DevicePickerSheetTest {

    @get:Rule
    val rule = createComposeRule()

    private val control = DevicesControl(
        picker = DevicePicker(
            thisDeviceCurrent = false,
            others = listOf(DevicePickerRow("mac-00001", "MacBook", DeviceKind.COMPUTER, current = true, status = "Playing")),
        ),
        thisDeviceName = "Pixel 8",
        thisDeviceKind = DeviceKind.PHONE,
        onChoose = {},
    )

    private val remote = PlayerUiState(
        trackId = 1,
        title = "Nightswim",
        artist = "R.E.M.",
        isPlaying = true,
        durationMs = 215_000,
        mode = PlaybackMode.Remote,
        deviceLine = "Playing on MacBook",
        deviceKind = DeviceKind.COMPUTER,
        devicesAvailable = true,
    )

    @Test
    fun aSheetThatLeftWithItsControlDoesNotComeBackByItself() {
        var devices by mutableStateOf<DevicesControl?>(control)
        rule.setContent {
            MusicHoarderTheme(darkTheme = false) {
                MiniPlayer(
                    state = remote,
                    coverUrl = null,
                    onExpand = {},
                    onPlayPause = {},
                    onNext = {},
                    devices = devices,
                )
            }
        }

        rule.onNodeWithText("Playing on MacBook").performClick()
        rule.onNodeWithText("Play on").assertExists()

        devices = null
        rule.waitForIdle()
        rule.onNodeWithText("Play on").assertDoesNotExist()

        devices = control
        rule.waitForIdle()
        rule.onNodeWithText("Play on").assertDoesNotExist()
    }
}
