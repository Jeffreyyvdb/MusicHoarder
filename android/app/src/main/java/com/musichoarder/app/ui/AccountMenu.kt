package com.musichoarder.app.ui

import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.size
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.rounded.Logout
import androidx.compose.material.icons.rounded.AccountCircle
import androidx.compose.material.icons.rounded.Check
import androidx.compose.material.icons.rounded.QrCodeScanner
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import com.musichoarder.app.data.AccountsState
import com.musichoarder.app.ui.theme.MhTheme

/**
 * The account switcher in the library top bar: every account remembered on this phone, plus
 * "Add account" (scans another pairing QR — each account is its own pairing) and signing the
 * active account out. Replaces the bare unpair button; with a single account signing out still
 * degrades to exactly that.
 */
@Composable
fun AccountMenu(
    accounts: AccountsState,
    onSwitchAccount: (Int) -> Unit,
    onAddAccountScanned: (String) -> Unit,
    onScanError: (String) -> Unit,
    onUnpair: () -> Unit,
) {
    val colors = MhTheme.colors
    val context = LocalContext.current
    var expanded by remember { mutableStateOf(false) }

    Box {
        MhIconButton(Icons.Rounded.AccountCircle, "Accounts", onClick = { expanded = true })
        DropdownMenu(
            expanded = expanded,
            onDismissRequest = { expanded = false },
            containerColor = colors.popover,
        ) {
            accounts.accounts.forEachIndexed { index, account ->
                // The secondary line: the email under a display name, with the role appended for
                // non-owner accounts ("friend@x.com · Friend"). Owners (and pre-role pairings)
                // are the default, so their line stays just the email.
                val role = account.role
                val detail = when {
                    role == null || role.equals("Owner", ignoreCase = true) -> account.email
                    else -> listOfNotNull(account.email, role).joinToString(" · ")
                }
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
            HorizontalDivider(color = colors.border)
            DropdownMenuItem(
                text = {
                    Text(
                        "Add account",
                        style = MaterialTheme.typography.bodyMedium,
                        color = colors.foreground,
                    )
                },
                leadingIcon = {
                    Icon(
                        Icons.Rounded.QrCodeScanner,
                        contentDescription = null,
                        tint = colors.mutedForeground,
                        modifier = Modifier.size(18.dp),
                    )
                },
                onClick = {
                    expanded = false
                    launchPairingScan(context, onScanned = onAddAccountScanned, onError = onScanError)
                },
            )
            DropdownMenuItem(
                text = {
                    Text(
                        "Sign out of this account",
                        style = MaterialTheme.typography.bodyMedium,
                        color = colors.foreground,
                    )
                },
                leadingIcon = {
                    Icon(
                        Icons.AutoMirrored.Rounded.Logout,
                        contentDescription = null,
                        tint = colors.mutedForeground,
                        modifier = Modifier.size(18.dp),
                    )
                },
                onClick = {
                    expanded = false
                    onUnpair()
                },
            )
        }
    }
}
