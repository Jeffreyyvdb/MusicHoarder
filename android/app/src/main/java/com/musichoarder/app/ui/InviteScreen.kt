package com.musichoarder.app.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.rounded.Check
import androidx.compose.material.icons.rounded.Close
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import com.musichoarder.app.data.InviteLink
import com.musichoarder.app.ui.theme.MhTheme

/** What the invite flow is showing. Every state carries the [link] so retry never re-parses. */
sealed interface InviteUiState {
    val link: InviteLink

    data class Loading(override val link: InviteLink) : InviteUiState

    data class Ready(
        override val link: InviteLink,
        val inviterName: String,
        val email: String?,
    ) : InviteUiState

    /** The accept call is in flight — the single-use token is being consumed right now. */
    data class Accepting(
        override val link: InviteLink,
        val inviterName: String,
        val email: String?,
    ) : InviteUiState

    data class Failed(
        override val link: InviteLink,
        val message: String,
        /** True when the invite itself is dead (consumed/expired/revoked) — retrying cannot help. */
        val gone: Boolean,
    ) : InviteUiState
}

/**
 * An https invite link opened in the app. Opening only *peeks* — the single-use token is consumed
 * exclusively by the explicit Accept tap, which pairs this phone as the new Friend account.
 */
@Composable
fun InviteScreen(
    state: InviteUiState,
    /** The host this phone is paired with now, for the re-point warning. Null when unpaired. */
    currentHost: String?,
    onAccept: () -> Unit,
    onDismiss: () -> Unit,
    onRetry: () -> Unit,
    modifier: Modifier = Modifier,
) {
    val colors = MhTheme.colors

    Column(modifier = modifier.fillMaxSize().background(colors.background)) {
        when (state) {
            is InviteUiState.Loading -> MessagePane("Loading invite…")
            is InviteUiState.Failed ->
                if (state.gone) {
                    CenteredPane {
                        Column(horizontalAlignment = Alignment.CenterHorizontally) {
                            Text(
                                state.message,
                                style = MaterialTheme.typography.bodyMedium,
                                color = colors.mutedForeground,
                                textAlign = TextAlign.Center,
                            )
                            Spacer(Modifier.height(16.dp))
                            PillButton("Close", Icons.Rounded.Close, filled = false, onClick = onDismiss)
                        }
                    }
                } else {
                    ErrorPane(state.message, onRetry)
                }
            is InviteUiState.Accepting -> MessagePane("Accepting the invite…")
            is InviteUiState.Ready -> CenteredPane {
                Column(
                    modifier = Modifier.padding(horizontal = 32.dp),
                    horizontalAlignment = Alignment.CenterHorizontally,
                ) {
                    Text(
                        "${state.inviterName} invited you",
                        style = MaterialTheme.typography.headlineSmall,
                        color = colors.foreground,
                        textAlign = TextAlign.Center,
                    )
                    Spacer(Modifier.height(8.dp))
                    Text(
                        buildString {
                            append("Accept to listen to the music shared with ")
                            append(state.email ?: "you")
                            append(" on ")
                            append(state.link.origin.substringAfter("://"))
                            append(".")
                        },
                        style = MaterialTheme.typography.bodyMedium,
                        color = colors.mutedForeground,
                        textAlign = TextAlign.Center,
                    )
                    if (currentHost != null) {
                        Spacer(Modifier.height(8.dp))
                        Text(
                            // The same consequence the pairing dialog spells out: one pairing per
                            // phone, so accepting replaces what this app is showing now.
                            "Accepting will replace the library this phone is paired with ($currentHost) and stop playback.",
                            style = MaterialTheme.typography.bodySmall,
                            color = colors.mutedForeground,
                            textAlign = TextAlign.Center,
                        )
                    }
                    Spacer(Modifier.height(20.dp))
                    Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                        PillButton("Accept", Icons.Rounded.Check, filled = true, onClick = onAccept)
                        PillButton("Not now", Icons.Rounded.Close, filled = false, onClick = onDismiss)
                    }
                }
            }
        }
    }
}
