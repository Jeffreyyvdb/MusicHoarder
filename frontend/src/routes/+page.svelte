<script lang="ts">
  import { page } from '$app/state';
  import SeoHead from '$lib/components/SeoHead.svelte';
  import JsonLd from '$lib/components/JsonLd.svelte';
  import LandingNav from '$lib/components/landing/LandingNav.svelte';
  import HeroSection from '$lib/components/landing/HeroSection.svelte';
  import ListenSection from '$lib/components/landing/ListenSection.svelte';
  import OrganizeSection from '$lib/components/landing/OrganizeSection.svelte';
  import ReviewSection from '$lib/components/landing/ReviewSection.svelte';
  import GrowSection from '$lib/components/landing/GrowSection.svelte';
  import ShareSection from '$lib/components/landing/ShareSection.svelte';
  import QuickstartSection from '$lib/components/landing/QuickstartSection.svelte';
  import FinalCtaSection from '$lib/components/landing/FinalCtaSection.svelte';
  import Footer from '$lib/components/landing/Footer.svelte';
  import {
    buildSoftwareApplicationSchema,
    organizationSchema,
    faqPageSchema
  } from '$lib/components/landing/structured-data';

  const title = 'MusicHoarder — Self-Hosted Music Library Organizer & Player';
  const description =
    'Point MusicHoarder at a messy music folder and get back a clean library on your own disk. It identifies every track by its sound, agrees on the metadata across several providers, and plays the result in an Apple Music-style app for iPhone, Android and desktop. Self-hosted, open source and free.';

  const appVersion = $derived(page.data.appVersion as string | null | undefined);
  const softwareApplicationSchema = $derived(buildSoftwareApplicationSchema(appVersion));
</script>

<SeoHead
  {title}
  {description}
  path="/"
  ogImageAlt="MusicHoarder on a desktop and an iPhone: the Albums grid of a real library next to Now Playing with synced lyrics"
/>

<JsonLd data={softwareApplicationSchema} />
<JsonLd data={organizationSchema} />
<JsonLd data={faqPageSchema} />

<!-- `overflow-x-clip`, not `hidden`: hidden makes <main> a scroll container, which would stop the
     nav bar sticking to the viewport. -->
<main class="bg-background text-foreground min-h-dvh overflow-x-clip">
  <LandingNav />
  <HeroSection />
  <ListenSection />
  <OrganizeSection />
  <ReviewSection />
  <GrowSection />
  <ShareSection />
  <QuickstartSection />
  <FinalCtaSection />
  <Footer />
</main>
