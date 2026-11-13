package com.musichoarder.app.data

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class PasskeySignalTest {
    @Test
    fun readsTheErrorCodeOfARefusal() {
        assertEquals("unknown_credential", PasskeySignal.errorCode("""{"error":"unknown_credential"}"""))
        assertEquals("invalid_assertion", PasskeySignal.errorCode("""{"error":"invalid_assertion"}"""))
        assertNull(PasskeySignal.errorCode("not json"))
        assertNull(PasskeySignal.errorCode("""{"ok":false}"""))
    }

    @Test
    fun buildsTheSignalFromTheRequestAndTheAssertion() {
        val request = """{"challenge":"abc","rpId":"musichoarder.app","allowCredentials":[]}"""
        val assertion = """{"id":"cred-123","rawId":"cred-123","type":"public-key","response":{}}"""
        assertEquals(
            """{"rpId":"musichoarder.app","credentialId":"cred-123"}""",
            PasskeySignal.unknownCredentialRequestJson(request, assertion),
        )
    }

    @Test
    fun buildsNoSignalWithoutAnRpIdOrACredentialId() {
        assertNull(PasskeySignal.unknownCredentialRequestJson("""{"challenge":"abc"}""", """{"id":"x"}"""))
        assertNull(PasskeySignal.unknownCredentialRequestJson("""{"rpId":"a"}""", """{"type":"public-key"}"""))
    }
}
