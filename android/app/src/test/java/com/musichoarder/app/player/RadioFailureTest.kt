package com.musichoarder.app.player

import com.musichoarder.app.data.ApiException
import com.musichoarder.app.data.NotPairedException
import com.musichoarder.app.data.UnauthorizedException
import java.io.IOException
import java.net.SocketTimeoutException
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Which failed station requests end the station. A failure that could pass must not: ending the
 * station on one left a shuffled album silent for good after a single dropped connection. The web
 * player's `isRefusal` draws the same line.
 */
class RadioFailureTest {
    @Test
    fun `a failure that could pass keeps the station`() {
        assertTrue(isTransientRadioFailure(IOException("connection reset")))
        assertTrue(isTransientRadioFailure(SocketTimeoutException()))
        assertTrue(isTransientRadioFailure(ApiException(502, "Bad Gateway")))
        assertTrue(isTransientRadioFailure(ApiException(504, "Gateway Timeout")))
        assertTrue(isTransientRadioFailure(ApiException(408, "Request Timeout")))
        assertTrue(isTransientRadioFailure(ApiException(429, "Too Many Requests")))
    }

    @Test
    fun `a refusal ends it`() {
        assertFalse(isTransientRadioFailure(ApiException(400, "Bad Request")))
        assertFalse(isTransientRadioFailure(ApiException(403, "Forbidden")))
        assertFalse(isTransientRadioFailure(UnauthorizedException()))
        assertFalse(isTransientRadioFailure(NotPairedException()))
    }

    @Test
    fun `an answer that makes no sense ends it`() {
        assertFalse(isTransientRadioFailure(IllegalStateException("unexpected body")))
    }
}
