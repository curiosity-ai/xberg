use crate::text::utf8_validation;
use ahash::AHashSet;
use once_cell::sync::Lazy;
use regex::Regex;
use std::borrow::Cow;

/// Regular expression for matching excessive newlines (3 or more consecutive newlines).
static EXCESSIVE_NEWLINES_REGEX: Lazy<Regex> =
    Lazy::new(|| Regex::new(r"\n{3,}").expect("Excessive newlines regex pattern is valid and should compile"));

/// Regular expression for matching multiple consecutive spaces (2 or more).
static MULTIPLE_SPACES_REGEX: Lazy<Regex> =
    Lazy::new(|| Regex::new(r" {2,}").expect("Multiple spaces regex pattern is valid and should compile"));

/// Normalizes whitespace in text by collapsing multiple spaces into a single space.
///
/// # Arguments
/// * `text` - The input text with potentially multiple consecutive spaces
///
/// # Returns
/// A new `String` with multiple spaces collapsed to single spaces
pub(crate) fn normalize_spaces(text: &str) -> Cow<'_, str> {
    if MULTIPLE_SPACES_REGEX.is_match(text) {
        Cow::Owned(MULTIPLE_SPACES_REGEX.replace_all(text, " ").into_owned())
    } else {
        Cow::Borrowed(text)
    }
}

/// Reduces excessive newlines in text by collapsing 3+ consecutive newlines into 2.
///
/// # Arguments
/// * `text` - The input text with potentially excessive newlines
///
/// # Returns
/// A new `String` with excessive newlines normalized to at most 2 consecutive newlines
pub(crate) fn normalize_newlines(text: &str) -> Cow<'_, str> {
    if EXCESSIVE_NEWLINES_REGEX.is_match(text) {
        Cow::Owned(EXCESSIVE_NEWLINES_REGEX.replace_all(text, "\n\n").into_owned())
    } else {
        Cow::Borrowed(text)
    }
}

/// Removes stopwords from text while preserving important patterns.
///
/// This function intelligently filters out common stopwords while preserving:
/// - All-uppercase words (acronyms)
/// - Words containing digits
/// - Words matching custom preserve patterns
/// - Single-letter words
/// - Words with non-alphabetic characters
///
/// # Arguments
/// * `text` - The input text to filter
/// * `stopwords` - Set of stopwords to remove (should be lowercase)
/// * `preserve_patterns` - Regex patterns for words that should never be removed
///
/// # Returns
/// A new `String` with stopwords removed
pub(crate) fn remove_stopwords(text: &str, stopwords: &AHashSet<String>, preserve_patterns: &[Regex]) -> String {
    let words: Vec<&str> = text.split_whitespace().collect();
    let mut filtered_words = Vec::with_capacity((words.len() as f32 * 0.7).ceil() as usize);

    for word in words {
        if word.is_empty() {
            continue;
        }

        if should_preserve_word(word, preserve_patterns) {
            filtered_words.push(word);
            continue;
        }

        if word.len() > 1 && word.bytes().all(|b| b.is_ascii_uppercase() || !b.is_ascii_alphabetic()) {
            filtered_words.push(word);
            continue;
        }

        if word.bytes().any(|b| b.is_ascii_digit()) {
            filtered_words.push(word);
            continue;
        }

        let clean_word = if word.is_ascii() {
            let clean_bytes: Vec<u8> = word
                .bytes()
                .filter(|&b| b.is_ascii_alphabetic())
                .map(|b| b.to_ascii_lowercase())
                .collect();
            utf8_validation::string_from_utf8(clean_bytes).unwrap_or_else(|_| {
                word.chars()
                    .filter(|c| c.is_alphabetic())
                    .collect::<String>()
                    .to_lowercase()
            })
        } else {
            word.chars()
                .filter(|c| c.is_alphabetic())
                .collect::<String>()
                .to_lowercase()
        };

        if clean_word.is_empty() {
            filtered_words.push(word);
            continue;
        }

        if clean_word.len() <= 1 {
            filtered_words.push(word);
            continue;
        }

        if !stopwords.contains(&clean_word) {
            filtered_words.push(word);
        }
    }

    filtered_words.join(" ")
}

/// Checks if a word should be preserved based on configured patterns.
///
/// # Arguments
/// * `word` - The word to check
/// * `preserve_patterns` - Regex patterns for words that should be preserved
///
/// # Returns
/// `true` if the word matches any preserve pattern, `false` otherwise
#[inline]
pub(crate) fn should_preserve_word(word: &str, preserve_patterns: &[Regex]) -> bool {
    preserve_patterns.iter().any(|pattern| pattern.is_match(word))
}

#[cfg(all(test, feature = "stopwords"))]
mod tests {
    use super::*;

    fn create_test_stopwords() -> AHashSet<String> {
        let mut set = AHashSet::new();
        set.insert("the".to_string());
        set.insert("is".to_string());
        set.insert("a".to_string());
        set.insert("and".to_string());
        set.insert("with".to_string());
        set.insert("by".to_string());
        set
    }

    #[test]
    fn test_normalize_spaces() {
        let input = "Text  with    multiple     spaces";
        let result = normalize_spaces(input);
        assert!(!result.contains("  "));
        assert!(result.contains("Text with multiple spaces"));
    }

    #[test]
    fn test_normalize_spaces_no_change() {
        let input = "Text with single spaces";
        let result = normalize_spaces(input);
        assert_eq!(result, input);
    }

    #[test]
    fn test_normalize_newlines() {
        let input = "Paragraph 1\n\n\n\n\nParagraph 2";
        let result = normalize_newlines(input);
        assert!(!result.contains("\n\n\n"));
        assert!(result.contains("Paragraph 1"));
        assert!(result.contains("Paragraph 2"));
    }

    #[test]
    fn test_normalize_newlines_no_change() {
        let input = "Paragraph 1\n\nParagraph 2";
        let result = normalize_newlines(input);
        assert_eq!(result, input);
    }

    #[test]
    fn test_remove_stopwords() {
        let stopwords = create_test_stopwords();
        let preserve_patterns = vec![];

        let input = "The quick brown fox is jumping over the lazy dog";
        let result = remove_stopwords(input, &stopwords, &preserve_patterns);

        assert!(!result.contains(" the "));
        assert!(!result.contains(" is "));
        assert!(result.contains("quick"));
        assert!(result.contains("brown"));
        assert!(result.contains("fox"));
    }

    #[test]
    fn test_remove_stopwords_preserves_uppercase() {
        let stopwords = create_test_stopwords();
        let preserve_patterns = vec![];

        let input = "The API is working WITH the SDK";
        let result = remove_stopwords(input, &stopwords, &preserve_patterns);

        assert!(result.contains("API"));
        assert!(result.contains("SDK"));
        assert!(result.contains("WITH"));
        assert!(!result.contains("The "));
        assert!(!result.contains(" is "));
    }

    #[test]
    fn test_remove_stopwords_preserves_numbers() {
        let stopwords = create_test_stopwords();
        let preserve_patterns = vec![];

        let input = "The version is 3.14 and the count is 42";
        let result = remove_stopwords(input, &stopwords, &preserve_patterns);

        assert!(result.contains("3.14"));
        assert!(result.contains("42"));
        assert!(result.contains("version"));
        assert!(result.contains("count"));
    }

    #[cfg_attr(coverage, ignore = "coverage instrumentation disables SIMD stopword paths")]
    #[test]
    fn test_remove_stopwords_handles_punctuation() {
        let stopwords = create_test_stopwords();
        let preserve_patterns = vec![];

        let input = "Hello, the world! This is great.";
        let result = remove_stopwords(input, &stopwords, &preserve_patterns);

        assert!(result.contains("Hello,"));
        assert!(result.contains("world!"));
        assert!(result.contains("great."));
    }

    #[test]
    fn test_remove_stopwords_single_letter() {
        let stopwords = create_test_stopwords();
        let preserve_patterns = vec![];

        let input = "I a x test";
        let result = remove_stopwords(input, &stopwords, &preserve_patterns);

        assert!(result.contains("I"));
        assert!(result.contains("x"));
    }

    #[test]
    fn test_preserve_patterns() {
        let stopwords = create_test_stopwords();
        let preserve_patterns = vec![
            Regex::new(r"\b[A-Z]{2,}\b").unwrap(),
            Regex::new(r"\b\d+\.\d+\.\d+\b").unwrap(),
            Regex::new(r"@\w+").unwrap(),
        ];

        let input = "The NASA and HTTP protocols version 1.2.3 by @john";
        let result = remove_stopwords(input, &stopwords, &preserve_patterns);

        assert!(result.contains("NASA"));
        assert!(result.contains("HTTP"));
        assert!(result.contains("1.2.3"));
        assert!(result.contains("@john"));

        assert!(!result.contains(" the "));
        assert!(!result.contains(" and "));
        assert!(!result.contains(" by "));
    }

    #[test]
    fn test_should_preserve_word() {
        let patterns = vec![Regex::new(r"\b[A-Z]{2,}\b").unwrap()];

        assert!(should_preserve_word("NASA", &patterns));
        assert!(should_preserve_word("HTTP", &patterns));
        assert!(!should_preserve_word("hello", &patterns));
    }

    #[test]
    fn test_lazy_regex_initialization() {
        let _ = &*EXCESSIVE_NEWLINES_REGEX;
        let _ = &*MULTIPLE_SPACES_REGEX;
    }
}
