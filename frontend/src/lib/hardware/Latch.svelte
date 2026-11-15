<script lang="ts">
  // A latching key (the skin's checkbox with an icon on its cap): it stays down while on. The label
  // only names it for screen readers.
  import { Checkbox, CheckboxIndicator } from '@ui-sdk/svelte/checkbox';
  import Icon, { type IconName } from './Icon.svelte';

  let {
    label,
    icon,
    checked,
    disabled,
    onCheckedChange
  }: {
    label: string;
    icon: IconName;
    checked: boolean;
    disabled?: boolean;
    onCheckedChange: (checked: boolean) => void;
  } = $props();
</script>

<label data-kit="check" data-icon="">
  <Checkbox
    data-kit="checkbox"
    {checked}
    {disabled}
    onCheckedChange={(next) => onCheckedChange(next === true)}
  >
    <CheckboxIndicator data-kit="checkbox-indicator" keepMounted />
    <span data-kit="check-icon" aria-hidden="true"><Icon name={icon} /></span>
  </Checkbox>
  <span data-kit="visually-hidden">{label}</span>
</label>
