package com.musichoarder.app.data

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * The multi-account store's pure core: serialization, the legacy single-pairing migration, and the
 * list mutations behind add / switch / remove. Pinned here because [SessionStore] persists exactly
 * these functions' output, and a decode regression would silently unpair every account.
 */
class AccountsStateTest {

    private fun account(
        baseUrl: String = "https://musichoarder.app",
        token: String = "tok-1",
        role: String? = "Owner",
        userId: String? = "user-1",
        email: String? = "owner@example.com",
        displayName: String? = "Owner",
    ) = StoredAccount(baseUrl, token, role, userId, email, displayName)

    // ── serialization ─────────────────────────────────────────────────────────

    @Test
    fun `encode-decode round-trips one account`() {
        val state = AccountsState(listOf(account()), 0)
        assertEquals(state, decodeAccountsState(encodeAccountsState(state)))
    }

    @Test
    fun `encode-decode round-trips four accounts and the active index`() {
        val state = AccountsState(
            listOf(
                account(userId = "u1", email = "a@x.com"),
                account(userId = "u2", email = "b@x.com", role = "Friend"),
                account(userId = "u3", email = "c@x.com", baseUrl = "https://other.example"),
                account(userId = "u4", email = "d@x.com", role = null, displayName = null),
            ),
            activeIndex = 2,
        )
        assertEquals(state, decodeAccountsState(encodeAccountsState(state)))
    }

    @Test
    fun `null, blank and corrupt json decode as empty`() {
        assertEquals(AccountsState(), decodeAccountsState(null))
        assertEquals(AccountsState(), decodeAccountsState(""))
        assertEquals(AccountsState(), decodeAccountsState("not-json"))
        assertEquals(AccountsState(), decodeAccountsState("""{"accounts": 12}"""))
    }

    @Test
    fun `decode clamps an out-of-range active index`() {
        val raw = encodeAccountsState(AccountsState(listOf(account()), 0))
            .replace("\"activeIndex\":0", "\"activeIndex\":7")
        assertEquals(0, decodeAccountsState(raw).activeIndex)
    }

    @Test
    fun `decode drops entries missing a token or url`() {
        val state = AccountsState(listOf(account(), account(token = "", userId = "u2")), 0)
        val decoded = decodeAccountsState(encodeAccountsState(state))
        assertEquals(1, decoded.accounts.size)
        assertEquals("tok-1", decoded.accounts[0].token)
    }

    // ── legacy migration ──────────────────────────────────────────────────────

    @Test
    fun `legacy keys migrate to a single active account`() {
        val state = migrateLegacyAccount("https://musichoarder.app", "legacy-token", "Friend")
        assertEquals(1, state!!.accounts.size)
        assertEquals(0, state.activeIndex)
        val migrated = state.active!!
        assertEquals("legacy-token", migrated.token)
        assertEquals("Friend", migrated.role)
        assertNull(migrated.userId)
    }

    @Test
    fun `legacy migration without a token yields nothing`() {
        assertNull(migrateLegacyAccount("https://musichoarder.app", null, null))
        assertNull(migrateLegacyAccount(null, "tok", null))
        assertNull(migrateLegacyAccount("", "", null))
    }

    // ── adding (pairing) ──────────────────────────────────────────────────────

    @Test
    fun `adding a second account appends it and makes it active`() {
        val friend = account(token = "tok-2", role = "Friend", userId = "u2", email = "friend@x.com")
        val state = AccountsState(listOf(account()), 0).adding(friend)
        assertEquals(2, state.accounts.size)
        assertEquals(1, state.activeIndex)
        assertEquals(friend, state.active)
    }

    @Test
    fun `re-adding the same account replaces it in place with the fresh token`() {
        val state = AccountsState(listOf(account(), account(userId = "u2", token = "tok-2")), 1)
            .adding(account(token = "renewed"))
        assertEquals(2, state.accounts.size)
        assertEquals("renewed", state.accounts[0].token)
        assertEquals(0, state.activeIndex)
    }

    @Test
    fun `same user on a different server is a separate account`() {
        val elsewhere = account(baseUrl = "https://other.example")
        val state = AccountsState(listOf(account()), 0).adding(elsewhere)
        assertEquals(2, state.accounts.size)
    }

    @Test
    fun `pre-userId accounts dedupe by email, identity-less ones by token`() {
        val legacy = account(userId = null)
        val byEmail = AccountsState(listOf(legacy), 0).adding(account(userId = null, token = "tok-9"))
        assertEquals(1, byEmail.accounts.size)
        assertEquals("tok-9", byEmail.accounts[0].token)

        val anonymous = account(userId = null, email = null)
        val byToken = AccountsState(listOf(anonymous), 0).adding(account(userId = null, email = null))
        assertEquals(1, byToken.accounts.size)
    }

    // ── switching ─────────────────────────────────────────────────────────────

    @Test
    fun `switching changes only the active index`() {
        val state = AccountsState(listOf(account(), account(userId = "u2", token = "t2")), 0)
        assertEquals(1, state.switchedTo(1).activeIndex)
        assertEquals(state.accounts, state.switchedTo(1).accounts)
    }

    @Test
    fun `switching to a stale index is a no-op`() {
        val state = AccountsState(listOf(account()), 0)
        assertEquals(state, state.switchedTo(4))
        assertEquals(state, state.switchedTo(-1))
    }

    // ── removing (sign-out and 401 eviction) ──────────────────────────────────

    @Test
    fun `removing the active account promotes the next one`() {
        val second = account(userId = "u2", token = "t2")
        val state = AccountsState(listOf(account(), second), 0).removingAt(0)
        assertEquals(listOf(second), state.accounts)
        assertEquals(0, state.activeIndex)
        assertEquals(second, state.active)
    }

    @Test
    fun `removing a parked account keeps the active one active`() {
        val first = account()
        val state = AccountsState(listOf(first, account(userId = "u2", token = "t2")), 0).removingAt(1)
        assertEquals(first, state.active)
    }

    @Test
    fun `removing an account before the active one shifts the index`() {
        val activeAccount = account(userId = "u2", token = "t2")
        val state = AccountsState(listOf(account(), activeAccount), 1).removingAt(0)
        assertEquals(activeAccount, state.active)
    }

    @Test
    fun `removing the last account empties the state`() {
        val state = AccountsState(listOf(account()), 0).removingAt(0)
        assertTrue(state.accounts.isEmpty())
        assertEquals(-1, state.activeIndex)
        assertNull(state.active)
    }

    // ── labels ────────────────────────────────────────────────────────────────

    @Test
    fun `label prefers display name, then email, then the host`() {
        assertEquals("Owner", account().label)
        assertEquals("owner@example.com", account(displayName = null).label)
        assertEquals("musichoarder.app", account(displayName = null, email = null).label)
    }
}
