---
keywords: [tips, misc, quick, useFrontendTool, render, null, duplicate, text]
---

# Misc Notes

Quick tips and small findings.

---

## useFrontendTool: render cannot return null

The `render` function must return a ReactElement or string, not `null`. TypeScript will complain.

```tsx
// Wrong - Type error
render: ({ args }) => {
  if (!args.data) return null;  // Error!
  return <Card data={args.data} />;
}

// Correct
render: ({ args }) => {
  if (!args.data) return <></>;  // Empty fragment
  return <Card data={args.data} />;
}
```

---

## useFrontendTool: AI duplicates rendered content in text

When using `useFrontendTool` with a `render` function, the AI often outputs text that duplicates what's already shown in the rendered component.

**Fix:** Add explicit instructions in the tool description:

```tsx
useFrontendTool({
  name: "showReport",
  description:
    "Display a report card. IMPORTANT: The tool renders a visual card " +
    "with all the data - do NOT repeat this information in your text response. " +
    "Just call the tool and optionally ask if the user needs anything else.",
  // ...
});
```

---

