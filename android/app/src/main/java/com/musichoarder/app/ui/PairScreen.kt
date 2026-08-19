package com.musichoarder.app.ui

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
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.rounded.ErrorOutline
import androidx.compose.material.icons.rounded.QrCodeScanner
import androidx.compose.material3.Button
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import com.google.mlkit.vision.barcode.common.Barcode
import com.google.mlkit.vision.codescanner.GmsBarcodeScannerOptions
import com.google.mlkit.vision.codescanner.GmsBarcodeScanning

/**
 * First run. The phone has no idea where the library lives, so it either scans the QR code the web
 * UI renders (Settings → Account → Mobile app) or takes the same two values by hand.
 */
@Composable
fun PairScreen(
    error: String?,
    onScanned: (String) -> Unit,
    onManual: (baseUrl: String, token: String) -> Unit,
    onError: (String) -> Unit,
    modifier: Modifier = Modifier,
) {
    val context = LocalContext.current
    var showManual by remember { mutableStateOf(false) }
    var baseUrl by remember { mutableStateOf("") }
    var token by remember { mutableStateOf("") }

    // The form has to stay reachable with the keyboard up: `imePadding` shrinks the box and the
    // column below scrolls, instead of the fields hiding behind the keyboard.
    Box(
        modifier = modifier.fillMaxSize().navigationBarsPadding().imePadding(),
        contentAlignment = Alignment.Center,
    ) {
        Column(
            modifier = Modifier
                .verticalScroll(rememberScrollState())
                .widthIn(max = 480.dp)
                .padding(horizontal = 28.dp, vertical = 32.dp),
            horizontalAlignment = Alignment.CenterHorizontally,
        ) {
            Icon(
                Icons.Rounded.QrCodeScanner,
                contentDescription = null,
                tint = MaterialTheme.colorScheme.primary,
                modifier = Modifier.size(56.dp),
            )
            Spacer(Modifier.height(20.dp))
            Text(
                "Connect to MusicHoarder",
                style = MaterialTheme.typography.headlineSmall,
                fontWeight = FontWeight.SemiBold,
                textAlign = TextAlign.Center,
            )
            Spacer(Modifier.height(10.dp))
            Text(
                "Open MusicHoarder in a browser and go to Settings → Account → Mobile app, then scan " +
                    "the pairing code it shows.",
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                textAlign = TextAlign.Center,
            )

            Spacer(Modifier.height(28.dp))

            Button(
                onClick = {
                    val options = GmsBarcodeScannerOptions.Builder()
                        .setBarcodeFormats(Barcode.FORMAT_QR_CODE)
                        .enableAutoZoom()
                        .build()
                    // Google's scanner runs out of process: no camera permission to request and no
                    // preview surface to own.
                    GmsBarcodeScanning.getClient(context, options).startScan()
                        .addOnSuccessListener { barcode ->
                            val value = barcode.rawValue
                            if (value.isNullOrBlank()) {
                                onError("That code was empty. Try scanning again.")
                            } else {
                                onScanned(value)
                            }
                        }
                        .addOnCanceledListener { }
                        .addOnFailureListener {
                            onError("The scanner is unavailable on this device — enter the details by hand instead.")
                            showManual = true
                        }
                },
                modifier = Modifier.fillMaxWidth(),
            ) {
                Icon(Icons.Rounded.QrCodeScanner, contentDescription = null, modifier = Modifier.size(18.dp))
                Spacer(Modifier.size(8.dp))
                Text("Scan pairing code")
            }

            if (error != null) {
                Spacer(Modifier.height(16.dp))
                Row(verticalAlignment = Alignment.Top) {
                    Icon(
                        Icons.Rounded.ErrorOutline,
                        contentDescription = null,
                        tint = MaterialTheme.colorScheme.error,
                        modifier = Modifier.size(18.dp),
                    )
                    Spacer(Modifier.size(8.dp))
                    Text(
                        error,
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.error,
                    )
                }
            }

            Spacer(Modifier.height(24.dp))
            HorizontalDivider()
            Spacer(Modifier.height(12.dp))

            if (!showManual) {
                OutlinedButton(onClick = { showManual = true }, modifier = Modifier.fillMaxWidth()) {
                    Text("Enter details manually")
                }
            } else {
                Column(
                    modifier = Modifier.fillMaxWidth(),
                    verticalArrangement = Arrangement.spacedBy(12.dp),
                ) {
                    OutlinedTextField(
                        value = baseUrl,
                        onValueChange = { baseUrl = it },
                        label = { Text("Server address") },
                        placeholder = { Text("https://musichoarder.app") },
                        singleLine = true,
                        keyboardOptions = KeyboardOptions(
                            keyboardType = KeyboardType.Uri,
                            imeAction = ImeAction.Next,
                        ),
                        modifier = Modifier.fillMaxWidth(),
                    )
                    OutlinedTextField(
                        value = token,
                        onValueChange = { token = it },
                        label = { Text("Access token") },
                        singleLine = true,
                        keyboardOptions = KeyboardOptions(imeAction = ImeAction.Done),
                        modifier = Modifier.fillMaxWidth(),
                    )
                    Button(
                        onClick = { onManual(baseUrl, token) },
                        modifier = Modifier.fillMaxWidth(),
                    ) {
                        Text("Connect")
                    }
                }
            }
        }
    }
}
