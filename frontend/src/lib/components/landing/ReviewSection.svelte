<script lang="ts">
  import LandingSection from '$lib/components/landing/LandingSection.svelte';
  import FeatureGrid from '$lib/components/landing/FeatureGrid.svelte';
  import Screenshot from '$lib/components/landing/Screenshot.svelte';
  import { reviewFeatures } from '$lib/components/landing/landing-content';
</script>

<LandingSection
  id="review"
  surface="grouped"
  eyebrow="Review"
  title="When it can’t decide, you do — and only then."
  lead="The confident matches flow straight into the library. Everything ambiguous collects in one Inbox, so you look at dozens of decisions, not thousands of files."
>
  <figure class="mt-12 md:mt-16">
    <Screenshot
      name="inbox-review"
      width={2200}
      height={1376}
      alt="Tag review on the desktop: a file named 63 (Dagger) (feat. MadeInTYO).mp3 identified by its fingerprint as Lean Wit Me at 100%, next to the other providers' candidates and a From to Will-write-to diff of the file's path"
    />
    <figcaption class="text-muted-foreground mt-4 text-center text-[15px] leading-[20px]">
      A file with a meaningless name, recognised by its sound — with every provider’s answer and
      exactly what will be written.
    </figcaption>
  </figure>

  <FeatureGrid items={reviewFeatures} surface="grouped" columns={2} class="mt-12 md:mt-16" />

  <div
    class="mt-16 grid grid-cols-1 items-center gap-8 md:mt-24 lg:grid-cols-[1fr_1.35fr] lg:gap-12"
  >
    <div class="text-center lg:order-first lg:text-left">
      <h3 class="text-[clamp(26px,3vw,34px)] leading-[1.12] font-bold tracking-[-0.025em] text-balance">
        Clear the Inbox from your phone.
      </h3>
      <p class="text-muted-foreground mt-4 text-[17px] leading-[1.5] text-pretty">
        Every queue opens as a list with pushed details and a bottom Accept · Skip · Reject toolbar,
        and the whole pipeline is a tap away under Manage.
      </p>
    </div>
    <Screenshot
      name="phone-manage"
      width={2400}
      height={1643}
      sizes="(min-width: 1024px) 680px, 100vw"
      alt="The installed app on three iPhones in dark mode: the Inbox with a count per queue, a Tag review decision with a bottom Accept toolbar, and the Pipeline summary"
    />
  </div>
</LandingSection>
