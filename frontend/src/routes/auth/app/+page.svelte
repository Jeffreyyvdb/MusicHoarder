<script lang="ts">
  import { onMount } from 'svelte';
  import { isNativeShell } from '#lib/native/shell.ts';
  import { takeAppSignInRequest } from '#lib/native/app-sign-in.ts';

  // Where the native app shell lands with the token of an emailed sign-in link
  // (`musichoarder://auth`, see `mobile/`). The token is spent only when a link was asked for from
  // this web view: the scheme is open to any page or app, and a token somebody else supplies would
  // sign this app in to their account. Anything else goes to the login page, which says the link
  // did not work.
  onMount(() => {
    const token = new URLSearchParams(location.search).get('token');
    const wanted = token && isNativeShell() && takeAppSignInRequest();
    location.replace(
      wanted ? `/auth/callback?token=${encodeURIComponent(token)}` : '/login?error=link'
    );
  });
</script>

<svelte:head>
  <title>Signing in · MusicHoarder</title>
  <meta name="robots" content="noindex" />
</svelte:head>
