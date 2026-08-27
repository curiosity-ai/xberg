```typescript title="TypeScript"
// Using fetch API
const fileInput = document.getElementById("file") as HTMLInputElement;
const file = fileInput.files?.[0];

if (file) {
  const formData = new FormData();
  formData.append("files", file);

  const response = await fetch("http://localhost:8000/extract", {
    method: "POST",
    body: formData,
  });

  const results = await response.json();
  console.log(results[0].content);
}
```
