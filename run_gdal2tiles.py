import argparse
import subprocess


def main():
    parser = argparse.ArgumentParser(description='Convert images to tiles. Check if gdal2tiles.py supports webp.')
    parser.add_argument('--format', type=str, choices=['png', 'webp'], default='png', help='Specify output image format')
    args = parser.parse_args()

    # Determine tile driver based on format
    tiledriver = 'WEBP' if args.format == 'webp' else 'PNG'

    # Call gdal2tiles.py with the selected format
    subprocess.run(['gdal2tiles.py', '--tiledriver', tiledriver])

    print(f'Tiles generated in {args.format} format.')


if __name__ == '__main__':
    main()