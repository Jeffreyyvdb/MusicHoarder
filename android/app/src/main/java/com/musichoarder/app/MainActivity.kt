package com.musichoarder.app

import android.content.Intent
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewmodel.compose.viewModel
import com.musichoarder.app.ui.AppViewModel
import com.musichoarder.app.ui.MusicHoarderRoot
import com.musichoarder.app.ui.theme.MusicHoarderTheme
import kotlinx.coroutines.flow.MutableStateFlow

class MainActivity : ComponentActivity() {
    /**
     * A `musichoarder://` link the system handed us — a pairing code from the phone's camera, Lens,
     * or any other QR reader, or the `musichoarder://auth` sign-in handoff from the browser. Held
     * as a flow rather than read straight off `intent` so a link that arrives while the app is
     * already running (onNewIntent) reaches Compose the same way a cold start does.
     */
    private val appLink = MutableStateFlow<String?>(null)

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        // Only on a genuine launch. A recreation (rotation, process restore) re-delivers the same
        // intent, and re-processing it re-opened the confirmation dialog — or, if the user had
        // unpaired in between, silently re-paired the phone from a stale link with no prompt.
        if (savedInstanceState == null) consumeAppLink(intent)

        setContent {
            MusicHoarderTheme {
                val viewModel: AppViewModel = viewModel()
                val link by appLink.collectAsStateWithLifecycle()

                // In a LaunchedEffect, not the composable body: handing the link to the view model
                // is a side effect, and composition can run any number of times.
                LaunchedEffect(link) {
                    link?.let {
                        viewModel.onAppLink(it)
                        appLink.value = null
                    }
                }

                MusicHoarderRoot(viewModel)
            }
        }
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        setIntent(intent)
        consumeAppLink(intent)
    }

    /**
     * Takes the link out of the intent as it is read, so the same scan cannot be replayed by a later
     * recreation that re-delivers it.
     */
    private fun consumeAppLink(intent: Intent?) {
        val link = intent?.dataString ?: return
        intent.data = null
        appLink.value = link
    }
}
