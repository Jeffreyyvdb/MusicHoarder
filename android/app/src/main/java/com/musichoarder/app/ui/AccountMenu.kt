package com.musichoarder.app.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.rounded.Logout
import androidx.compose.material.icons.rounded.Check
import androidx.compose.material.icons.rounded.PersonAdd
import androidx.compose.material.icons.rounded.Refresh
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import com.musichoarder.app.data.AccountsState
import com.musichoarder.app.data.StoredAccount
import com.musichoarder.app.ui.theme.MhMenuShape
import com.musichoarder.app.ui.theme.MhTheme

/**
 * The account entry point: an initials avatar at the end of every tab's header, opening every
 * account remembered on this phone, "Refresh library", "Add account" and signing the active account
 * out. The web's `AccountButton` — the App Store / Music pattern — which on a phone is the one place
 * the account lives, so it sits on every tab rather than on one settings page.
 *
 * "Refresh library" moved in here from its own top-bar button: it is rarely needed (the library
 * refetches on foreground), and TalkBack and switch users still need a path that is not a gesture.
 *
 * "Add account" opens the full sign-in screen, the way the web's switcher goes to `/login?switch`.
 * It used to launch the QR scanner directly, which quietly made a paired phone the one place
 * where email and passkey sign-in were unavailable — and a QR needs a second device the phone
 * cannot assume is nearby.
 */
@Composable
fun AccountMenu(
    accounts: AccountsState,
    onSwitchAccount: (Int) -> Unit,
    onAddAccount: () -> Unit,
    onUnpair: () -> Unit,
    onRefresh: () -> Unit,
    modifier: Modifier = Modifier,
) {
    val colors = MhTheme.colors
    var expanded by remember { mutableStateOf(false) }
    val active = accounts.active

    Box(modifier = modifier) {
        IconButton(
            onClick = { expanded = true },
            modifier = Modifier.semantics {
                contentDescription = active?.let { "Account: ${it.label}" } ?: "Account"
            },
        ) {
            AccountAvatar(active)
        }
        DropdownMenu(
            expanded = expanded,
            onDismissRequest = { expanded = false },
            shape = MhMenuShape,
            containerColor = colors.popover,
        ) {
            accounts.accounts.forEachIndexed { index, account ->
                val detail = accountDetail(account)
                DropdownMenuItem(
                    text = {
                        Column {
                            Text(
                                account.label,
                                style = MaterialTheme.typography.bodyMedium,
                                color = colors.foreground,
                            )
                            if (detail != null && detail != account.label) {
                                Text(
                                    detail,
                                    style = MaterialTheme.typography.bodySmall,
                                    color = colors.mutedForeground,
                                )
                            }
                        }
                    },
                    leadingIcon = { AccountAvatar(account, size = 28) },
                    trailingIcon = {
                        if (index == accounts.activeIndex) {
                            Icon(
                                Icons.Rounded.Check,
                                contentDescription = "Active account",
                                tint = colors.primary,
                                modifier = Modifier.size(16.dp),
                            )
                        }
                    },
                    onClick = {
                        expanded = false
                        onSwitchAccount(index)
                    },
                )
            }
            HorizontalDivider(color = colors.separator)
            MenuAction("Refresh library", Icons.Rounded.Refresh) {
                expanded = false
                onRefresh()
            }
            MenuAction("Add account", Icons.Rounded.PersonAdd) {
                expanded = false
                onAddAccount()
            }
            HorizontalDivider(color = colors.separator)
            // The web's "Sign out": its own group, in the destructive text colour, last.
            MenuAction(
                "Sign out of this account",
                Icons.AutoMirrored.Rounded.Logout,
                color = colors.destructiveText,
            ) {
                expanded = false
                onUnpair()
            }
        }
    }
}

@Composable
private fun MenuAction(
    label: String,
    icon: ImageVector,
    color: Color = MhTheme.colors.foreground,
    onClick: () -> Unit,
) {
    val colors = MhTheme.colors
    DropdownMenuItem(
        text = { Text(label, style = MaterialTheme.typography.bodyMedium, color = color) },
        leadingIcon = {
            Icon(
                icon,
                contentDescription = null,
                tint = if (color == colors.foreground) colors.mutedForeground else color,
                modifier = Modifier.size(20.dp),
            )
        },
        onClick = onClick,
    )
}

/**
 * The secondary line: the email under a display name, with the role appended for anyone who is not
 * the instance's owner ("friend@x.com · Member"). Owners (and pre-role pairings) are the default, so
 * their line stays just the email.
 *
 * The stored role is the legacy wire word `/auth/me` still sends for shipped builds
 * (`Owner`/`Demo`/`Friend`); only the *display* moves to the web's vocabulary, so a friend reads as
 * a Member on both clients and the stored value — which the API routes key on — is untouched.
 */
private fun accountDetail(account: StoredAccount): String? {
    val role = account.role
    if (role == null || role.equals("Owner", ignoreCase = true)) return account.email
    val shown = if (role.equals("Friend", ignoreCase = true)) "Member" else role
    return listOfNotNull(account.email, shown).joinToString(" · ")
}

/**
 * The initials avatar every account surface uses — a port of the web's `AccountPanel` avatar: the
 * first two characters of the display name (or the email) on a cyan ramp dark enough at its top
 * end that the white initials stay legible.
 */
@Composable
fun AccountAvatar(account: StoredAccount?, modifier: Modifier = Modifier, size: Int = 32) {
    val name = account?.displayName?.trim()?.takeIf(String::isNotEmpty)
    val initials = (name ?: account?.email ?: account?.label.orEmpty()).take(2).uppercase()
    // Sized in dp, not sp: the initials are part of a fixed-size badge, and at a large font scale
    // they would outgrow the circle (the web pins them at 13px for the same reason).
    val fontSize = with(LocalDensity.current) { (size * 13f / 32f).dp.toSp() }
    Box(
        modifier = modifier
            .size(size.dp)
            .background(AvatarGradient, CircleShape),
        contentAlignment = Alignment.Center,
    ) {
        Text(
            initials,
            color = Color.White,
            fontSize = fontSize,
            fontWeight = FontWeight.SemiBold,
            maxLines = 1,
        )
    }
}

/** `bg-gradient-to-br from-cyan-800 to-cyan-500`. */
private val AvatarGradient = Brush.linearGradient(
    colors = listOf(Color(0xFF155E75), Color(0xFF06B6D4)),
    start = Offset.Zero,
    end = Offset.Infinite,
)
