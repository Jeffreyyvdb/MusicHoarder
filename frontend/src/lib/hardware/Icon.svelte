<script lang="ts" module>
  // The kit's glyphs, from IBM's Carbon set (Apache-2.0, ./CARBON-LICENSE), inlined as path data so it needs no
  // React icon package. Only the ones the devices print.
  const paths = {
    play: [
      'M7,28a1,1,0,0,1-1-1V5a1,1,0,0,1,1.4819-.8763l20,11a1,1,0,0,1,0,1.7525l-20,11A1.0005,1.0005,0,0,1,7,28Z'
    ],
    pause: [
      'M12,6H10A2,2,0,0,0,8,8V24a2,2,0,0,0,2,2h2a2,2,0,0,0,2-2V8a2,2,0,0,0-2-2Z',
      'M22,6H20a2,2,0,0,0-2,2V24a2,2,0,0,0,2,2h2a2,2,0,0,0,2-2V8a2,2,0,0,0-2-2Z'
    ],
    'skip-back': [
      'M27,28a.9975.9975,0,0,1-.501-.1348l-19-11a1,1,0,0,1,0-1.73l19-11A1,1,0,0,1,28,5V27a1,1,0,0,1-1,1Z',
      'M2 4H4V28H2z'
    ],
    'skip-forward': [
      'M28 4H30V28H28z',
      'M5,28a1,1,0,0,1-1-1V5a1,1,0,0,1,1.501-.8652l19,11a1,1,0,0,1,0,1.73l-19,11A.9975.9975,0,0,1,5,28Z'
    ],
    heart: [
      'M22.5,4c-2,0-3.9,0.8-5.3,2.2L16,7.4l-1.1-1.1C12,3.3,7.2,3.3,4.3,6.2c0,0-0.1,0.1-0.1,0.1c-3,3-3,7.8,0,10.8L16,29l11.8-11.9c3-3,3-7.8,0-10.8C26.4,4.8,24.5,4,22.5,4z'
    ],
    'volume-mute': [
      'M31 12.41 29.59 11 26 14.59 22.41 11 21 12.41 24.59 16 21 19.59 22.41 21 26 17.41 29.59 21 31 19.59 27.41 16 31 12.41z',
      'M18,30a1,1,0,0,1-.71-.3L9.67,22H3a1,1,0,0,1-1-1H2V11a1,1,0,0,1,1-1H9.67l7.62-7.7a1,1,0,0,1,1.41,0A1,1,0,0,1,19,3V29A1,1,0,0,1,18,30ZM4,20h6a1.17,1.17,0,0,1,.79.3L17,26.57V5.43L10.79,11.7A1.17,1.17,0,0,1,10,12H4Z'
    ],
    maximize: [
      'M20 2V4h6.586L18 12.586 19.414 14 28 5.414V12h2V2H20z',
      'M14 19.414 12.586 18 4 26.586V20H2V30H12V28H5.414L14 19.414z'
    ],
    shuffle: [
      'M22.59,19.41,26.17,23H19.55l-4.37-7,4.37-7h6.62l-3.58,3.59L24,14l6-6L24,2,22.59,3.41,26.17,7H19.55a2,2,0,0,0-1.69.94L14,14.11,10.14,7.94A2,2,0,0,0,8.45,7H2V9H8.45l4.37,7L8.45,23H2v2H8.45a2,2,0,0,0,1.69-.94L14,17.89l3.86,6.17a2,2,0,0,0,1.69.94h6.62l-3.58,3.59L24,30l6-6-6-6Z'
    ]
  } as const;

  export type IconName = keyof typeof paths;
</script>

<script lang="ts">
  let { name, size = 16 }: { name: IconName; size?: number } = $props();
</script>

<span data-kit="icon" data-icon={name} aria-hidden="true">
  <svg viewBox="0 0 32 32" width={size} height={size} fill="currentColor" focusable="false">
    {#each paths[name] as d (d)}<path {d} />{/each}
  </svg>
</span>
