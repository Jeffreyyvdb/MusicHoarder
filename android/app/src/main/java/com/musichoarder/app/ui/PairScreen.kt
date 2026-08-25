package com.musichoarder.app.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.rounded.ErrorOutline
import androidx.compose.material.icons.rounded.MailOutline
import androidx.compose.material.icons.rounded.QrCodeScanner
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.OutlinedTextFieldDefaults
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import com.musichoarder.app.ui.theme.MhTheme

/**
 * First run. The phone has no idea where the library lives, so it signs in with the account's
 * email address (the server emails a one-time link that deep-links back into the app — no other
 * device needed), scans the QR code the web UI renders (Settings → Account → Mobile app), or
 * takes the base URL + token by hand.
 */
@Composable
fun PairScreen(
    error: String?,
    emailSentTo: String?,
    onScanned: (String) -> Unit,
    onManual: (baseUrl: String, token: String) -> Unit,
    onRequestEmailLink: (baseUrl: String, email: String) -> Unit,
    onError: (String) -> Unit,
    modifier: Modifier = Modifier,
) {
    val colors = MhTheme.colors
    val context = LocalContext.current
    var showManual by remember { mutableStateOf(false) }
    var baseUrl by remember { mutableStateOf("") }
    var email by remember { mutableStateOf("") }
    var token by remember { mutableStateOf("") }

    // The form has to stay reachable with the keyboard up: `imePadding` shrinks the box and the
    // column below scrolls, instead of the fields hiding behind the keyboard.
    Box(
        modifier = modifier
            .fillMaxSize()
            .background(colors.background)
            .navigationBarsPadding()
            .imePadding(),
        contentAlignment = Alignment.Center,
    ) {
        Column(
            modifier = Modifier
                .verticalScroll(rememberScrollState())
                .widthIn(max = 460.dp)
                .padding(horizontal = 24.dp, vertical = 32.dp),
            horizontalAlignment = Alignment.CenterHorizontally,
        ) {
            // The app mark: the brand green on its own tinted plate, as the web sidebar renders it.
            Box(
                modifier = Modifier
                    .size(52.dp)
                    .clip(RoundedCornerShape(14.dp))
                    .background(colors.primary.copy(alpha = 0.14f)),
                contentAlignment = Alignment.Center,
            ) {
                Icon(
                    Icons.Rounded.QrCodeScanner,
                    contentDescription = null,
                    tint = colors.primary,
                    modifier = Modifier.size(26.dp),
                )
            }

            Spacer(Modifier.height(20.dp))
            Text(
                "Connect to MusicHoarder",
                style = MaterialTheme.typography.headlineSmall,
                color = colors.foreground,
                textAlign = TextAlign.Center,
            )
            Spacer(Modifier.height(8.dp))
            Text(
                "Sign in with your account email and we'll send a link to this phone — or scan " +
                    "the pairing code from Settings → Account → Mobile app.",
                style = MaterialTheme.typography.bodySmall,
                color = colors.mutedForeground,
                textAlign = TextAlign.Center,
            )

            Spacer(Modifier.height(26.dp))

            Column(
                modifier = Modifier.fillMaxWidth(),
                verticalArrangement = Arrangement.spacedBy(10.dp),
            ) {
                PairField(
                    value = baseUrl,
                    onValueChange = { baseUrl = it },
                    label = "Server address",
                    placeholder = "https://musichoarder.app",
                    keyboardOptions = KeyboardOptions(
                        keyboardType = KeyboardType.Uri,
                        imeAction = ImeAction.Next,
                    ),
                )
                PairField(
                    value = email,
                    onValueChange = { email = it },
                    label = "Email address",
                    placeholder = "you@example.com",
                    keyboardOptions = KeyboardOptions(
                        keyboardType = KeyboardType.Email,
                        imeAction = ImeAction.Done,
                    ),
                )
                Spacer(Modifier.height(2.dp))
                PrimaryButton(
                    label = if (emailSentTo == null) "Email me a sign-in link" else "Send a new link",
                    icon = Icons.Rounded.MailOutline,
                    modifier = Modifier.fillMaxWidth(),
                ) { onRequestEmailLink(baseUrl, email) }
            }

            if (emailSentTo != null) {
                Spacer(Modifier.height(14.dp))
                Row(
                    modifier = Modifier
                        .fillMaxWidth()
                        .clip(RoundedCornerShape(10.dp))
                        .background(colors.primary.copy(alpha = 0.12f))
                        .border(1.dp, colors.primary.copy(alpha = 0.4f), RoundedCornerShape(10.dp))
                        .padding(horizontal = 12.dp, vertical = 10.dp),
                    verticalAlignment = Alignment.Top,
                ) {
                    Icon(
                        Icons.Rounded.MailOutline,
                        contentDescription = null,
                        tint = colors.primary,
                        modifier = Modifier.size(16.dp),
                    )
                    Spacer(Modifier.size(8.dp))
                    Text(
                        "If $emailSentTo has an account, a sign-in link is on its way. Open the " +
                            "email on this phone and tap the link to finish.",
                        style = MaterialTheme.typography.bodySmall,
                        color = colors.foreground,
                    )
                }
            }

            if (error != null) {
                Spacer(Modifier.height(14.dp))
                Row(
                    modifier = Modifier
                        .fillMaxWidth()
                        .clip(RoundedCornerShape(10.dp))
                        .background(colors.destructive.copy(alpha = 0.12f))
                        .border(1.dp, colors.destructive.copy(alpha = 0.4f), RoundedCornerShape(10.dp))
                        .padding(horizontal = 12.dp, vertical = 10.dp),
                    verticalAlignment = Alignment.Top,
                ) {
                    Icon(
                        Icons.Rounded.ErrorOutline,
                        contentDescription = null,
                        tint = colors.destructive,
                        modifier = Modifier.size(16.dp),
                    )
                    Spacer(Modifier.size(8.dp))
                    Text(
                        error,
                        style = MaterialTheme.typography.bodySmall,
                        color = colors.destructive,
                    )
                }
            }

            Spacer(Modifier.height(22.dp))
            HorizontalDivider(color = colors.border)
            Spacer(Modifier.height(14.dp))

            OutlineButton(
                label = "Scan pairing code",
                modifier = Modifier.fillMaxWidth(),
            ) {
                launchPairingScan(
                    context,
                    onScanned = onScanned,
                    onError = { message ->
                        onError(message)
                        showManual = true
                    },
                )
            }

            Spacer(Modifier.height(14.dp))

            if (!showManual) {
                OutlineButton(
                    label = "Enter an access token instead",
                    modifier = Modifier.fillMaxWidth(),
                ) { showManual = true }
            } else {
                // Reuses the server-address field above; only the token is extra here.
                Column(
                    modifier = Modifier.fillMaxWidth(),
                    verticalArrangement = Arrangement.spacedBy(10.dp),
                ) {
                    PairField(
                        value = token,
                        onValueChange = { token = it },
                        label = "Access token",
                        placeholder = "",
                        keyboardOptions = KeyboardOptions(imeAction = ImeAction.Done),
                    )
                    Spacer(Modifier.height(2.dp))
                    PrimaryButton(
                        label = "Connect",
                        icon = null,
                        modifier = Modifier.fillMaxWidth(),
                    ) { onManual(baseUrl, token) }
                }
            }
        }
    }
}

@Composable
private fun PairField(
    value: String,
    onValueChange: (String) -> Unit,
    label: String,
    placeholder: String,
    keyboardOptions: KeyboardOptions,
) {
    val colors = MhTheme.colors
    OutlinedTextField(
        value = value,
        onValueChange = onValueChange,
        label = { Text(label, style = MaterialTheme.typography.bodySmall) },
        placeholder = {
            if (placeholder.isNotEmpty()) {
                Text(placeholder, style = MaterialTheme.typography.bodyMedium, color = colors.mutedForeground)
            }
        },
        singleLine = true,
        shape = RoundedCornerShape(8.dp),
        keyboardOptions = keyboardOptions,
        textStyle = MaterialTheme.typography.bodyMedium,
        colors = OutlinedTextFieldDefaults.colors(
            focusedTextColor = colors.foreground,
            unfocusedTextColor = colors.foreground,
            focusedBorderColor = colors.ring,
            unfocusedBorderColor = colors.border,
            focusedLabelColor = colors.mutedForeground,
            unfocusedLabelColor = colors.mutedForeground,
            cursorColor = colors.primary,
            focusedContainerColor = colors.input.copy(alpha = 0.6f),
            unfocusedContainerColor = colors.input.copy(alpha = 0.6f),
        ),
        modifier = Modifier.fillMaxWidth(),
    )
}

@Composable
private fun PrimaryButton(
    label: String,
    icon: androidx.compose.ui.graphics.vector.ImageVector?,
    modifier: Modifier = Modifier,
    onClick: () -> Unit,
) {
    val colors = MhTheme.colors
    Row(
        modifier = modifier
            .clip(CircleShape)
            .background(colors.primary)
            .clickable(onClick = onClick)
            .padding(vertical = 13.dp),
        horizontalArrangement = Arrangement.Center,
        verticalAlignment = Alignment.CenterVertically,
    ) {
        if (icon != null) {
            Icon(
                icon,
                contentDescription = null,
                tint = colors.primaryForeground,
                modifier = Modifier.size(17.dp),
            )
            Spacer(Modifier.size(8.dp))
        }
        Text(
            label,
            style = MaterialTheme.typography.labelLarge,
            fontWeight = FontWeight.SemiBold,
            color = colors.primaryForeground,
        )
    }
}

@Composable
private fun OutlineButton(label: String, modifier: Modifier = Modifier, onClick: () -> Unit) {
    val colors = MhTheme.colors
    Row(
        modifier = modifier
            .clip(CircleShape)
            .background(Color.Transparent)
            .border(1.dp, colors.border, CircleShape)
            .clickable(onClick = onClick)
            .padding(vertical = 13.dp),
        horizontalArrangement = Arrangement.Center,
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Text(
            label,
            style = MaterialTheme.typography.labelLarge,
            fontWeight = FontWeight.Medium,
            color = colors.foreground,
        )
    }
}
