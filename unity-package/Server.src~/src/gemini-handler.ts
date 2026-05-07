/**
 * Gemini API handler for Nano Banana (image generation).
 * Calls Google's Gemini API to generate images and returns base64 PNG data.
 */

interface GeminiImageRequest {
  apiKey: string;
  model: string;
  prompt: string;
  referenceImage?: string; // base64 PNG
}

interface GeminiImageResponse {
  success: boolean;
  imageBase64?: string;
  description?: string;
  error?: string;
}

/**
 * Generate an image using the Gemini API (Nano Banana).
 */
export async function generateImage(
  request: GeminiImageRequest
): Promise<GeminiImageResponse> {
  const { apiKey, model, prompt, referenceImage } = request;

  if (!apiKey) {
    return {
      success: false,
      error:
        "Gemini API key not configured. Please set it in AI Chat > Settings.",
    };
  }

  const url = `https://generativelanguage.googleapis.com/v1beta/models/${model}:generateContent?key=${apiKey}`;

  // Build request body
  const parts: Array<Record<string, unknown>> = [];

  // Add reference image if provided
  if (referenceImage) {
    parts.push({
      inlineData: {
        mimeType: "image/png",
        data: referenceImage,
      },
    });
  }

  // Add text prompt — if reference image is present, instruct Gemini to edit it
  const finalPrompt = referenceImage
    ? `Edit the provided image according to these instructions: ${prompt}`
    : prompt;
  parts.push({
    text: finalPrompt,
  });

  const body = {
    contents: [
      {
        parts,
      },
    ],
    generationConfig: {
      responseModalities: ["TEXT", "IMAGE"],
    },
  };

  try {
    console.error(
      `[NanoBanana] Generating image with ${model}: "${prompt.substring(0, 60)}..."`
    );

    const response = await fetch(url, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body),
    });

    if (!response.ok) {
      const errorText = await response.text();
      let errorMsg = `Gemini API error (${response.status})`;
      try {
        const errorJson = JSON.parse(errorText);
        errorMsg = errorJson.error?.message || errorMsg;
      } catch {
        errorMsg += `: ${errorText.substring(0, 200)}`;
      }
      return { success: false, error: errorMsg };
    }

    const data = (await response.json()) as {
      candidates?: Array<{
        content?: {
          parts?: Array<{
            text?: string;
            inlineData?: { mimeType: string; data: string };
          }>;
        };
      }>;
    };

    // Extract image and text from response
    let imageBase64: string | undefined;
    let description: string | undefined;

    const candidates = data.candidates;
    if (candidates && candidates.length > 0) {
      const parts = candidates[0].content?.parts;
      if (parts) {
        for (const part of parts) {
          if (part.inlineData?.data) {
            imageBase64 = part.inlineData.data;
          }
          if (part.text) {
            description = part.text;
          }
        }
      }
    }

    if (!imageBase64) {
      return {
        success: false,
        error:
          "Gemini returned no image data. The model may not support image generation, or the prompt was filtered.",
        ...(description ? { description } : {}),
      };
    }

    console.error("[NanoBanana] Image generated successfully.");
    return {
      success: true,
      imageBase64,
      description: description || "Generated image",
    };
  } catch (err) {
    const msg = err instanceof Error ? err.message : String(err);
    console.error(`[NanoBanana] Error: ${msg}`);
    return { success: false, error: `Gemini request failed: ${msg}` };
  }
}
