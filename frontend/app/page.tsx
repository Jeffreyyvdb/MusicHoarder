import { HeroSection } from "@/components/landing/hero-section"
import { BeforeAfterSection } from "@/components/landing/before-after-section"
import { HowItWorksSection } from "@/components/landing/how-it-works-section"
import { FeaturesSection } from "@/components/landing/features-section"
import { TechStackSection } from "@/components/landing/tech-stack-section"
import { CTASection } from "@/components/landing/cta-section"
import { Footer } from "@/components/landing/footer"
import { StructuredData } from "@/components/landing/structured-data"

export default function Home() {
  return (
    <main className="min-h-screen bg-background">
      <StructuredData />
      <HeroSection />
      <BeforeAfterSection />
      <HowItWorksSection />
      <FeaturesSection />
      <TechStackSection />
      <CTASection />
      <Footer />
    </main>
  )
}
