<script lang="ts">
  import SeoHead from '$lib/components/SeoHead.svelte';
  import JsonLd from '$lib/components/JsonLd.svelte';
  import HeroSection from '$lib/components/landing/HeroSection.svelte';
  import BeforeAfterSection from '$lib/components/landing/BeforeAfterSection.svelte';
  import HowItWorksSection from '$lib/components/landing/HowItWorksSection.svelte';
  import FeaturesSection from '$lib/components/landing/FeaturesSection.svelte';
  import TechStackSection from '$lib/components/landing/TechStackSection.svelte';
  import CTASection from '$lib/components/landing/CTASection.svelte';
  import Footer from '$lib/components/landing/Footer.svelte';
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

<main class="bg-background min-h-screen">
  <HeroSection />
  <BeforeAfterSection />
  <HowItWorksSection />
  <FeaturesSection />
  <TechStackSection />
  <CTASection />
  <Footer />
</main>
