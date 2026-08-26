package com.musichoarder.app.ui

import android.content.Context
import com.google.mlkit.vision.barcode.common.Barcode
import com.google.mlkit.vision.codescanner.GmsBarcodeScannerOptions
import com.google.mlkit.vision.codescanner.GmsBarcodeScanning

/**
 * Launches the out-of-process Google code scanner for a pairing QR — no camera permission to
 * request and no preview surface to own. Called from [PairScreen], which serves both first run and
 * the account switcher's "Add account". Cancelling the scanner calls neither callback.
 */
fun launchPairingScan(
    context: Context,
    onScanned: (String) -> Unit,
    onError: (String) -> Unit,
) {
    val options = GmsBarcodeScannerOptions.Builder()
        .setBarcodeFormats(Barcode.FORMAT_QR_CODE)
        .enableAutoZoom()
        .build()
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
        }
}
