import SwiftUI
import UIKit
import WidgetBridge
import WidgetKit

extension Color {
    /// The surface the system's own widgets sit on: white, and a raised black in Dark Mode. A
    /// tinted or clear Home Screen drops it and keeps only what is drawn on top.
    static let widgetSurface = Color(uiColor: UIColor { traits in
        traits.userInterfaceStyle == .dark ? UIColor(white: 0.11, alpha: 1) : .white
    })
}

extension WidgetStore {
    /// A stored cover, decoded no larger than `side` points at 3x: WidgetKit archives the views
    /// it is handed, bitmaps included, and refuses an archive that is too heavy.
    func cover(_ art: ArtFile?, side: CGFloat) -> UIImage? {
        guard let art, let image = UIImage(contentsOfFile: url(of: art).path) else { return nil }
        let shrink = side * 3 / max(image.size.width, image.size.height)
        guard shrink < 1 else { return image }
        let size = CGSize(width: image.size.width * shrink, height: image.size.height * shrink)
        return image.preparingThumbnail(of: size) ?? image
    }
}

/// An album cover in a rounded square, or a note where there is none.
struct CoverView: View {
    let image: UIImage?
    var cornerRadius: CGFloat = 8

    var body: some View {
        Color.clear
            .aspectRatio(1, contentMode: .fit)
            .overlay { picture }
            .clipShape(RoundedRectangle(cornerRadius: cornerRadius, style: .continuous))
            .accessibilityHidden(true)
    }

    @ViewBuilder private var picture: some View {
        if let image {
            Image(uiImage: image)
                .resizable()
                .keepingItsShadesWhenTinted()
                .aspectRatio(contentMode: .fill)
        } else {
            ZStack {
                Rectangle().fill(.quaternary)
                Image(systemName: "music.note")
                    .font(.system(size: 20, weight: .semibold))
                    .foregroundStyle(.secondary)
            }
        }
    }
}

private extension Image {
    /// On a tinted or clear Home Screen (iOS 18) the system draws an image as a flat silhouette
    /// unless told otherwise, which turns a cover into a blank square. Desaturated keeps the
    /// picture, in greys the tint sits well beside.
    @ViewBuilder func keepingItsShadesWhenTinted() -> some View {
        if #available(iOS 18.0, *) {
            widgetAccentedRenderingMode(.desaturated)
        } else {
            self
        }
    }
}

/// The app's mark, the record from its icon, drawn so that it takes a tint: a tinted Home Screen
/// colours a view by its alpha alone, and an icon image would come out as a filled square.
struct AppGlyph: View {
    var size: CGFloat = 20

    var body: some View {
        ZStack {
            // The disc, from its edge in to the label.
            Circle().strokeBorder(lineWidth: size / 2 * (48.0 / 70.0))
            // The spindle.
            Circle().frame(width: size * (8.0 / 70.0), height: size * (8.0 / 70.0))
        }
        .frame(width: size, height: size)
        .foregroundStyle(.primary)
        .widgetAccentable()
        .accessibilityHidden(true)
    }
}

/// What a widget says when it has nothing to draw.
struct WidgetNotice: View {
    let title: String
    var detail: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 2) {
            AppGlyph(size: 22)
            Spacer(minLength: 8)
            Text(title)
                .font(.system(size: 15, weight: .bold))
                .foregroundStyle(.primary)
                .widgetAccentable()
            if let detail {
                Text(detail)
                    .font(.system(size: 12))
                    .foregroundStyle(.secondary)
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
    }
}
