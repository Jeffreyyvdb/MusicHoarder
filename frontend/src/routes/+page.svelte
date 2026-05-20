<script lang="ts">
  import SeoHead from '$lib/components/SeoHead.svelte';
  import JsonLd from '$lib/components/JsonLd.svelte';
  import LandingNav from '$lib/components/landing/LandingNav.svelte';
  import HeroSection from '$lib/components/landing/HeroSection.svelte';
  import FeaturesSection from '$lib/components/landing/FeaturesSection.svelte';
  import HowItWorksSection from '$lib/components/landing/HowItWorksSection.svelte';
  import TechStackSection from '$lib/components/landing/TechStackSection.svelte';
  import Footer from '$lib/components/landing/Footer.svelte';
  import MobileLanding from '$lib/components/mobile/MobileLanding.svelte';
  import {
    softwareApplicationSchema,
    organizationSchema,
    faqPageSchema
  } from '$lib/components/landing/structured-data';

  const title = 'MusicHoarder — Fix Your Messy Music Library';
  const description =
    'Self-hosted, open-source tool that identifies, enriches, and organizes your entire music collection automatically — AcoustID fingerprinting, MusicBrainz + Spotify metadata, synced lyrics.';
</script>

<SeoHead {title} {description} path="/" />

<JsonLd data={softwareApplicationSchema} />
<JsonLd data={organizationSchema} />
<JsonLd data={faqPageSchema} />

<div class="md:hidden">
  <MobileLanding />
</div>

<main class="bg-background text-foreground hidden min-h-screen overflow-x-hidden pb-12 md:block">
  <LandingNav />
  <HeroSection />
  <FeaturesSection />
  <HowItWorksSection />
  <TechStackSection />
  <Footer />
</main>
