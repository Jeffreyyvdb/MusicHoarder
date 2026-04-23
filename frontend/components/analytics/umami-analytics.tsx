import Script from 'next/script'

export function UmamiAnalytics() {
  const websiteId = process.env.NEXT_PUBLIC_UMAMI_WEBSITE_ID
  const scriptSrc = process.env.NEXT_PUBLIC_UMAMI_SRC
  const recorderSrc = process.env.NEXT_PUBLIC_UMAMI_RECORDER_SRC

  if (!websiteId || !scriptSrc) {
    return null
  }

  return (
    <>
      <Script
        defer
        src={scriptSrc}
        data-website-id={websiteId}
        strategy="afterInteractive"
      />
      {recorderSrc ? (
        <Script
          defer
          src={recorderSrc}
          data-website-id={websiteId}
          data-sample-rate="1"
          data-mask-level="moderate"
          data-max-duration="300000"
          strategy="afterInteractive"
        />
      ) : null}
    </>
  )
}
