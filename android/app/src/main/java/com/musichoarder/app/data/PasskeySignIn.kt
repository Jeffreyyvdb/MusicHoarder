package com.musichoarder.app.data

import android.content.Context
import androidx.credentials.CredentialManager
import androidx.credentials.GetCredentialRequest
import androidx.credentials.GetPublicKeyCredentialOption
import androidx.credentials.PublicKeyCredential
import androidx.credentials.exceptions.GetCredentialCancellationException
import androidx.credentials.exceptions.GetCredentialException
import androidx.credentials.exceptions.NoCredentialException

/** The user backed out of the system passkey sheet. Not a failure to report — just a no-op. */
class PasskeyCancelledException : Exception("Passkey sign-in was cancelled.")

/** Credential Manager could not produce an assertion, with a reason worth showing. */
class PasskeyUnavailableException(message: String) : Exception(message)

/**
 * The system passkey sheet, wrapped so the sign-in flow deals in "assertion JSON, or a message".
 *
 * The passkey belongs to the *web* origin — it is the one enrolled in the browser under
 * Settings → Account. Android only lets this app speak for that origin when the origin's
 * `/.well-known/assetlinks.json` carries a `get_login_creds` statement naming this package and its
 * signing fingerprint (see the frontend's `assetlinks.ts`). Without it the sheet reports no usable
 * credential, which is why [NoCredentialException] gets an explanation rather than a generic
 * failure: it is far and away the most likely first-run outcome, and nothing in the app can fix it.
 */
object PasskeySignIn {
    /**
     * Runs the ceremony for [requestJson] — the server's WebAuthn request options, verbatim — and
     * returns Credential Manager's response JSON, to be posted straight back to the server.
     *
     * [activityContext] has to be the Activity, not the Application: the system draws the passkey
     * sheet over it. It is used for the duration of the call and never held.
     */
    suspend fun authenticate(activityContext: Context, requestJson: String): String {
        val request = GetCredentialRequest(listOf(GetPublicKeyCredentialOption(requestJson)))
        val response = try {
            CredentialManager.create(activityContext).getCredential(activityContext, request)
        } catch (e: GetCredentialCancellationException) {
            throw PasskeyCancelledException()
        } catch (e: NoCredentialException) {
            throw PasskeyUnavailableException(
                "No passkey for this server is available on this phone. Create one in the web app " +
                    "under Settings → Account, and make sure the server publishes its Android app link."
            )
        } catch (e: GetCredentialException) {
            throw PasskeyUnavailableException(e.errorMessage?.toString() ?: "Passkey sign-in failed.")
        }

        val credential = response.credential as? PublicKeyCredential
            ?: throw PasskeyUnavailableException("The phone returned a credential that is not a passkey.")
        return credential.authenticationResponseJson
    }
}
