<script lang="ts">
  import { page } from '$app/state';
  import SeoHead from '$lib/components/SeoHead.svelte';
  import JsonLd from '$lib/components/JsonLd.svelte';
  import LandingNav from '$lib/components/landing/LandingNav.svelte';
  import HeroSection from '$lib/components/landing/HeroSection.svelte';
  import PipelineConveyor from '$lib/components/landing/PipelineConveyor.svelte';
  import InboxShowcase from '$lib/components/landing/InboxShowcase.svelte';
  import LibraryShowcase from '$lib/components/landing/LibraryShowcase.svelte';
  import ProvenanceShowcase from '$lib/components/landing/ProvenanceShowcase.svelte';
  import QuickstartSection from '$lib/components/landing/QuickstartSection.svelte';
  import FeaturesSection from '$lib/components/landing/FeaturesSection.svelte';
  import FinalCtaSection from '$lib/components/landing/FinalCtaSection.svelte';
  import Footer from '$lib/components/landing/Footer.svelte';
  import {
    buildSoftwareApplicationSchema,
    organizationSchema,
    faqPageSchema
  } from '$lib/components/landing/structured-data';

  const title = 'MusicHoarder — Self-Hosted Music Library Organizer & Tagger';
  const description =
    'Point MusicHoarder at a messy folder and get back a clean library. This self-hosted, open-source pipeline fingerprints every track with AcoustID, reaches consensus across seven metadata providers, grades each match with an LLM, dedupes, and writes tidy files to your own disk.';

  const appVersion = $derived(page.data.appVersion as string | null | undefined);
  const softwareApplicationSchema = $derived(buildSoftwareApplicationSchema(appVersion));
</script>

<SeoHead {title} {description} path="/" />

<JsonLd data={softwareApplicationSchema} />
<JsonLd data={organizationSchema} />
<JsonLd data={faqPageSchema} />

<main class="bg-background text-foreground min-h-screen overflow-x-hidden pb-4">
  <LandingNav />
  <HeroSection />
  <PipelineConveyor />
  <InboxShowcase />
  <LibraryShowcase />
  <ProvenanceShowcase />
  <QuickstartSection />
  <FeaturesSection />
  <FinalCtaSection />
  <Footer />
</main>
