/** Triggers a browser file download for an in-memory blob (object URL + synthetic anchor click). */
export function downloadBlob(blob: Blob, filename: string): void {
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = filename;
  document.body.appendChild(a);
  a.click();
  a.remove();
  // Revoke on the next tick so the click has committed the navigation.
  setTimeout(() => URL.revokeObjectURL(url), 0);
}
